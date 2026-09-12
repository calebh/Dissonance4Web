/*
 * Non variadic wrappers around opus_encoder_ctl / opus_decoder_ctl.
 *
 * Dissonance calls Opus through these four names rather than through
 * opus_encoder_ctl directly, because the real functions are variadic and P/Invoke
 * cannot describe a variadic call. The opus builds Dissonance ships for desktop
 * and mobile carry these wrappers inside the library; the WebAssembly build is
 * plain upstream libopus, so they live here instead.
 *
 * Unity compiles this file as part of a WebGL build - a .c file under a folder
 * named WebGL is picked up automatically - so only libopus.a has to be built
 * ahead of time. See Native~/README.md.
 *
 * No opus header is included on purpose. Declaring the two entry points here
 * keeps the file buildable without an include path, which is what lets Unity
 * compile it with no configuration. The prototypes must stay variadic to match
 * how libopus was compiled, because that is what selects the calling convention.
 */

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
