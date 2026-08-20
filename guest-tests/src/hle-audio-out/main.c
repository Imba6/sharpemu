#include <stdint.h>

#define USER_ID 0x10000000
#define SAMPLE_RATE 48000u
#define BUFFER_FRAMES 256u
#define AUDIO_FORMAT_S16_STEREO 1  /* PS5 SCE_AUDIO_OUT_PARAM_FORMAT_S16_STEREO (0 is mono) */
#define TONE_HZ 440u
#define TONE_AMPLITUDE 7000
#define BUFFER_COUNT 188

static int16_t audio_buffer[BUFFER_FRAMES * 2];
static uint32_t phase;

int sceAudioOutInit(void);
int sceAudioOutOpen(
    int user_id,
    int type,
    int index,
    uint32_t buffer_length,
    uint32_t frequency,
    int format
);
int sceAudioOutOutput(
    int handle,
    const void *buffer
);
int sceAudioOutClose(
    int handle
);

static void fill_tone_buffer(void)
{
    for (uint32_t frame = 0; frame < BUFFER_FRAMES; frame++) {
        phase += TONE_HZ;

        if (phase >= SAMPLE_RATE) {
            phase -= SAMPLE_RATE;
        }

        int16_t sample =
            phase < (SAMPLE_RATE / 2)
                ? TONE_AMPLITUDE
                : -TONE_AMPLITUDE;

        audio_buffer[frame * 2 + 0] = sample;
        audio_buffer[frame * 2 + 1] = sample;
    }
}

int main(void)
{
    if (sceAudioOutInit() != 0) {
        __builtin_trap();
    }

    int handle = sceAudioOutOpen(
        USER_ID,
        0,
        0,
        BUFFER_FRAMES,
        SAMPLE_RATE,
        AUDIO_FORMAT_S16_STEREO
    );

    if (handle <= 0) {
        __builtin_trap();
    }

    for (int index = 0; index < BUFFER_COUNT; index++) {
        fill_tone_buffer();

        if (sceAudioOutOutput(
                handle,
                audio_buffer) != 0) {
            __builtin_trap();
        }
    }

    if (sceAudioOutClose(handle) != 0) {
        __builtin_trap();
    }

    return 0;
}
