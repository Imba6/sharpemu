// Stub SDL_mixer header for the VirtualPS5 DoomGeneric build.
//
// DoomGeneric's i_sound.c does `#include <SDL_mixer.h>` whenever FEATURE_SOUND
// is defined, but it does not call any SDL_mixer function itself — the SDL glue
// lives in i_sdlsound.c, which this port does not build. Our own sound backend
// (i_ps5sound.c) provides DG_sound_module/DG_music_module. This empty stub lets
// i_sound.c compile with FEATURE_SOUND enabled without pulling in real SDL.
#ifndef VPS5_STUB_SDL_MIXER_H
#define VPS5_STUB_SDL_MIXER_H
#endif
