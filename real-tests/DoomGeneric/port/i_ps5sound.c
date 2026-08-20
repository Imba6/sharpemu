// VirtualPS5 sound backend for DoomGeneric (SFX).
//
// Provides DoomGeneric's DG_sound_module without SDL/SDL_mixer. Doom's SFX are
// DMX "DS" lumps (8-bit unsigned PCM, mono, ~11 kHz). This backend decodes each
// lump once to signed 16-bit mono, then a dedicated guest audio thread mixes the
// active channels (per-channel step resampling to the output rate, with volume
// and stereo separation) into a static interleaved S16 stereo buffer and submits
// it through the PS5 userspace audio ABI:
//
//   Doom mixer -> guest PCM (static buffer) -> sceAudioOutOutput
//               -> VirtualPS5 AudioOut HLE  -> host audio backend
//
// The output grain is small and sceAudioOutOutput blocks for pacing, so the
// audio thread self-paces without a busy wait and never stalls the render loop.
// Music is not implemented (DG_music_module is a safe stub).

#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
#include <string.h>
#include <pthread.h>

#include "doomtype.h"
#include "i_sound.h"
#include "w_wad.h"
#include "z_zone.h"

// ---------------------------------------------------------------------------
// PS5 audio ABI (resolved by VirtualPS5 HLE at runtime). The HLE contract uses
// the real PS5 SCE_AUDIO_OUT_PARAM_FORMAT values: 1 = signed-16 stereo (frame =
// 4 bytes); sceAudioOutOutput reads buffer_length*4 bytes from the guest-mapped
// buffer and blocks for pacing. (Format 0 is S16 MONO on PS5.)
// ---------------------------------------------------------------------------
#define AUDIO_USER_ID           0x10000000
#define AUDIO_FORMAT_S16_STEREO 1
#define MIX_RATE                48000u   // output/mix sample rate (host-supported)
#define MIX_FRAMES              256u     // frames per submit (~5.33 ms grain)

int sceAudioOutInit(void);
int sceAudioOutOpen(int user_id, int type, int index,
                    uint32_t buffer_length, uint32_t frequency, int format);
int sceAudioOutOutput(int handle, const void *ptr);
int sceAudioOutClose(int handle);

#define NUM_CHANNELS 16

// A decoded sound effect: signed 16-bit mono at its native sample rate. Cached
// on sfxinfo->driver_data so each lump is decoded at most once.
typedef struct {
    int16_t *samples;
    uint32_t length;   // sample count
    uint32_t rate;     // native sample rate (Hz)
} ps5_sfx_t;

// One playing voice. pos/step are 16.16 fixed point so a sound at any native
// rate resamples to MIX_RATE by simple stepping.
typedef struct {
    const int16_t *samples;
    uint32_t length;
    uint32_t pos;       // 16.16 fixed sample index
    uint32_t step;      // 16.16 fixed increment = rate / MIX_RATE
    int leftvol;        // 0..255
    int rightvol;       // 0..255
    int active;
} ps5_channel_t;

static ps5_channel_t s_channels[NUM_CHANNELS];
static pthread_mutex_t s_mix_mutex = PTHREAD_MUTEX_INITIALIZER;

// Static (guest BSS) so sceAudioOutOutput can read it from the guest map.
static int16_t s_mixbuf[MIX_FRAMES * 2];

static int s_audio_handle = -1;
static volatile int s_audio_running = 0;
static pthread_t s_audio_thread;
static boolean s_use_sfx_prefix = true;
static int s_sound_ready = 0;

// Volume/separation -> per-side volume, matching Doom's stereo panning.
// vol is 0..127, sep is 0..254 (128 = centre).
static void PanVolumes(int vol, int sep, int *left, int *right)
{
    int l = ((254 - sep) * vol) / 127;
    int r = ((sep) * vol) / 127;
    if (l < 0) l = 0; else if (l > 255) l = 255;
    if (r < 0) r = 0; else if (r > 255) r = 255;
    *left = l;
    *right = r;
}

static int GetSfxLumpNum(sfxinfo_t *sfx)
{
    char namebuf[9];
    sfxinfo_t *s = sfx->link != NULL ? sfx->link : sfx;

    if (s_use_sfx_prefix) {
        snprintf(namebuf, sizeof(namebuf), "ds%s", s->name);
    } else {
        snprintf(namebuf, sizeof(namebuf), "%s", s->name);
    }

    // W_CheckNumForName returns -1 for a missing lump (no I_Error), so a WAD
    // that lacks a given SFX simply plays nothing instead of aborting.
    return W_CheckNumForName(namebuf);
}

// Decode the DMX DS lump for sfxinfo into 16-bit mono, caching on driver_data.
static ps5_sfx_t *DecodeSfx(sfxinfo_t *sfxinfo)
{
    if (sfxinfo->driver_data != NULL) {
        return (ps5_sfx_t *)sfxinfo->driver_data;
    }

    if (sfxinfo->lumpnum < 0) {
        sfxinfo->lumpnum = GetSfxLumpNum(sfxinfo);
    }
    if (sfxinfo->lumpnum < 0) {
        return NULL;
    }

    int lumpnum = sfxinfo->lumpnum;
    byte *data = W_CacheLumpNum(lumpnum, PU_STATIC);
    int lumplen = W_LumpLength(lumpnum);

    // DMX header: 0x03 0x00, u16 sample rate, u32 length.
    if (lumplen < 8 || data[0] != 0x03 || data[1] != 0x00) {
        W_ReleaseLumpNum(lumpnum);
        return NULL;
    }

    int rate = (data[3] << 8) | data[2];
    uint32_t length = ((uint32_t)data[7] << 24) | ((uint32_t)data[6] << 16) |
                      ((uint32_t)data[5] << 8) | (uint32_t)data[4];

    if (length > (uint32_t)(lumplen - 8) || length <= 48) {
        W_ReleaseLumpNum(lumpnum);
        return NULL;
    }

    // DMX pads the payload with 16 bytes at each end; the real samples start at
    // offset 24 (8 header + 16 lead) and run for length-32 bytes.
    const byte *pcm = data + 24;
    uint32_t count = length - 32;

    int16_t *out = (int16_t *)malloc((size_t)count * sizeof(int16_t));
    ps5_sfx_t *sfx = (ps5_sfx_t *)malloc(sizeof(ps5_sfx_t));
    if (out == NULL || sfx == NULL) {
        free(out);
        free(sfx);
        W_ReleaseLumpNum(lumpnum);
        return NULL;
    }

    // 8-bit unsigned -> 16-bit signed: (b * 257) - 32768, centred at silence.
    for (uint32_t i = 0; i < count; i++) {
        int sample = (pcm[i] | (pcm[i] << 8)) - 32768;
        out[i] = (int16_t)sample;
    }

    W_ReleaseLumpNum(lumpnum);

    sfx->samples = out;
    sfx->length = count;
    sfx->rate = rate > 0 ? (uint32_t)rate : 11025u;
    sfxinfo->driver_data = sfx;
    return sfx;
}

// Mix one grain of MIX_FRAMES stereo frames. Caller holds s_mix_mutex.
static void MixGrain(void)
{
    for (uint32_t f = 0; f < MIX_FRAMES; f++) {
        int left = 0;
        int right = 0;

        for (int c = 0; c < NUM_CHANNELS; c++) {
            ps5_channel_t *ch = &s_channels[c];
            if (!ch->active) {
                continue;
            }

            uint32_t index = ch->pos >> 16;
            if (index >= ch->length) {
                ch->active = 0;
                continue;
            }

            int sample = ch->samples[index];
            left += (sample * ch->leftvol) >> 8;
            right += (sample * ch->rightvol) >> 8;
            ch->pos += ch->step;
        }

        if (left > 32767) left = 32767; else if (left < -32768) left = -32768;
        if (right > 32767) right = 32767; else if (right < -32768) right = -32768;

        s_mixbuf[f * 2 + 0] = (int16_t)left;
        s_mixbuf[f * 2 + 1] = (int16_t)right;
    }
}

static void *AudioThread(void *arg)
{
    (void)arg;
    while (s_audio_running) {
        pthread_mutex_lock(&s_mix_mutex);
        MixGrain();
        pthread_mutex_unlock(&s_mix_mutex);

        // Blocks for pacing (host queue back-pressure); no busy wait.
        if (sceAudioOutOutput(s_audio_handle, s_mixbuf) != 0) {
            break;
        }
    }
    return NULL;
}

// ---------------------------------------------------------------------------
// sound_module_t entry points
// ---------------------------------------------------------------------------

static boolean I_PS5_InitSound(boolean use_sfx_prefix)
{
    s_use_sfx_prefix = use_sfx_prefix;
    memset(s_channels, 0, sizeof(s_channels));

    if (sceAudioOutInit() != 0) {
        return false;
    }

    s_audio_handle = sceAudioOutOpen(AUDIO_USER_ID, 0, 0,
                                     MIX_FRAMES, MIX_RATE, AUDIO_FORMAT_S16_STEREO);
    if (s_audio_handle <= 0) {
        return false;
    }

    s_audio_running = 1;
    if (pthread_create(&s_audio_thread, NULL, AudioThread, NULL) != 0) {
        s_audio_running = 0;
        sceAudioOutClose(s_audio_handle);
        s_audio_handle = -1;
        return false;
    }

    s_sound_ready = 1;
    return true;
}

static void I_PS5_ShutdownSound(void)
{
    if (!s_sound_ready) {
        return;
    }

    s_sound_ready = 0;
    s_audio_running = 0;
    pthread_join(s_audio_thread, NULL);

    if (s_audio_handle > 0) {
        sceAudioOutClose(s_audio_handle);
        s_audio_handle = -1;
    }
}

static int I_PS5_GetSfxLumpNum(sfxinfo_t *sfx)
{
    return GetSfxLumpNum(sfx);
}

static void I_PS5_UpdateSound(void)
{
    // Mixing runs on the dedicated audio thread; nothing to do per tic.
}

static void I_PS5_UpdateSoundParams(int channel, int vol, int sep)
{
    if (channel < 0 || channel >= NUM_CHANNELS) {
        return;
    }

    int left, right;
    PanVolumes(vol, sep, &left, &right);

    pthread_mutex_lock(&s_mix_mutex);
    s_channels[channel].leftvol = left;
    s_channels[channel].rightvol = right;
    pthread_mutex_unlock(&s_mix_mutex);
}

static int I_PS5_StartSound(sfxinfo_t *sfxinfo, int channel, int vol, int sep)
{
    if (!s_sound_ready || channel < 0 || channel >= NUM_CHANNELS) {
        return -1;
    }

    ps5_sfx_t *sfx = DecodeSfx(sfxinfo);
    if (sfx == NULL || sfx->length == 0) {
        return -1;
    }

    int left, right;
    PanVolumes(vol, sep, &left, &right);

    pthread_mutex_lock(&s_mix_mutex);
    ps5_channel_t *ch = &s_channels[channel];
    ch->samples = sfx->samples;
    ch->length = sfx->length;
    ch->pos = 0;
    ch->step = (uint32_t)(((uint64_t)sfx->rate << 16) / MIX_RATE);
    ch->leftvol = left;
    ch->rightvol = right;
    ch->active = 1;
    pthread_mutex_unlock(&s_mix_mutex);

    return channel;
}

static void I_PS5_StopSound(int channel)
{
    if (channel < 0 || channel >= NUM_CHANNELS) {
        return;
    }

    pthread_mutex_lock(&s_mix_mutex);
    s_channels[channel].active = 0;
    pthread_mutex_unlock(&s_mix_mutex);
}

static boolean I_PS5_SoundIsPlaying(int channel)
{
    if (channel < 0 || channel >= NUM_CHANNELS) {
        return false;
    }

    pthread_mutex_lock(&s_mix_mutex);
    int active = s_channels[channel].active;
    pthread_mutex_unlock(&s_mix_mutex);
    return active ? true : false;
}

static void I_PS5_CacheSounds(sfxinfo_t *sounds, int num_sounds)
{
    // Lazy: sounds are decoded on first StartSound. Nothing to precache.
    (void)sounds;
    (void)num_sounds;
}

static snddevice_t s_sfx_devices[] = {
    SNDDEVICE_SB,
    SNDDEVICE_PAS,
    SNDDEVICE_GUS,
    SNDDEVICE_WAVEBLASTER,
    SNDDEVICE_SOUNDCANVAS,
    SNDDEVICE_AWE32,
};

sound_module_t DG_sound_module = {
    s_sfx_devices,
    sizeof(s_sfx_devices) / sizeof(*s_sfx_devices),
    I_PS5_InitSound,
    I_PS5_ShutdownSound,
    I_PS5_GetSfxLumpNum,
    I_PS5_UpdateSound,
    I_PS5_UpdateSoundParams,
    I_PS5_StartSound,
    I_PS5_StopSound,
    I_PS5_SoundIsPlaying,
    I_PS5_CacheSounds,
};

// ---------------------------------------------------------------------------
// Music: not implemented. A safe stub so FEATURE_SOUND links; Init returns
// false, so Doom runs without music.
// ---------------------------------------------------------------------------

static boolean I_PS5_InitMusic(void) { return false; }
static void I_PS5_ShutdownMusic(void) {}
static void I_PS5_SetMusicVolume(int volume) { (void)volume; }
static void I_PS5_PauseMusic(void) {}
static void I_PS5_ResumeMusic(void) {}
static void *I_PS5_RegisterSong(void *data, int len) { (void)data; (void)len; return NULL; }
static void I_PS5_UnRegisterSong(void *handle) { (void)handle; }
static void I_PS5_PlaySong(void *handle, boolean looping) { (void)handle; (void)looping; }
static void I_PS5_StopSong(void) {}
static boolean I_PS5_MusicIsPlaying(void) { return false; }
static void I_PS5_PollMusic(void) {}

static snddevice_t s_music_devices[] = {
    SNDDEVICE_SB,
    SNDDEVICE_GENMIDI,
};

music_module_t DG_music_module = {
    s_music_devices,
    sizeof(s_music_devices) / sizeof(*s_music_devices),
    I_PS5_InitMusic,
    I_PS5_ShutdownMusic,
    I_PS5_SetMusicVolume,
    I_PS5_PauseMusic,
    I_PS5_ResumeMusic,
    I_PS5_RegisterSong,
    I_PS5_UnRegisterSong,
    I_PS5_PlaySong,
    I_PS5_StopSong,
    I_PS5_MusicIsPlaying,
    I_PS5_PollMusic,
};

// Globals normally provided by i_sdlsound.c; referenced by i_sound.c under
// FEATURE_SOUND. We do not use libsamplerate (simple step resampling instead).
int use_libsamplerate = 0;
float libsamplerate_scale = 0.65f;

// Provided by dummy.c only when FEATURE_SOUND is off; supply the no-op here.
void I_InitTimidityConfig(void)
{
}
