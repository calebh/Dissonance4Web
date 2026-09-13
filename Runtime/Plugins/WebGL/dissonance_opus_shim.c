/*
 * Non variadic wrappers around opus_encoder_ctl / opus_decoder_ctl.
 *
 * Dissonance calls Opus through these four names rather than through
 * opus_encoder_ctl directly, because the real functions are variadic and P/Invoke
 * cannot describe a variadic call. The opus builds Dissonance ships for desktop
 * and mobile carry these wrappers inside the library; the WebAssembly build is
 * plain upstream libopus, so they live here instead.
 *
 * Unity compiles this file into a WebGL player, so only libopus.a has to be built
 * ahead of time. See Native~/README.md.
 *
 * This file must reach WebGL builds and nothing else, and two things see to it.
 * The .meta file enables the plugin for WebGL only: the folder being named WebGL
 * does not restrict a source plugin, and Unity's default for one is to compile it
 * into every IL2CPP player. And the body below is compiled only by Emscripten, so
 * that if the importer settings are ever lost - a regenerated .meta, a copy into
 * another project - a desktop build gets an empty file rather than failing to
 * link, since desktop Opus lives in opus.dll and has no static opus_encoder_ctl.
 *
 * No opus header is included on purpose. Declaring the two entry points here
 * keeps the file buildable without an include path, which is what lets Unity
 * compile it with no configuration. The prototypes must stay variadic to match
 * how libopus was compiled, because that is what selects the calling convention.
 */

#ifdef __EMSCRIPTEN__

struct OpusEncoder;
struct OpusDecoder;

extern int opus_encoder_ctl(struct OpusEncoder *st, int request, ...);
extern int opus_decoder_ctl(struct OpusDecoder *st, int request, ...);

int dissonance_opus_encoder_ctl_in(struct OpusEncoder *st, int request, int value)
{
    return opus_encoder_ctl(st, request, value);
}

int dissonance_opus_encoder_ctl_out(struct OpusEncoder *st, int request, int *value)
{
    return opus_encoder_ctl(st, request, value);
}

int dissonance_opus_decoder_ctl_in(struct OpusDecoder *st, int request, int value)
{
    return opus_decoder_ctl(st, request, value);
}

int dissonance_opus_decoder_ctl_out(struct OpusDecoder *st, int request, int *value)
{
    return opus_decoder_ctl(st, request, value);
}

#else

/* ISO C forbids an empty translation unit, and some compilers warn about one. */
typedef int dissonance_opus_shim_is_webgl_only;

#endif
