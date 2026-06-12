# gmsb-sampler-ducking

Sidechain-style ducker for
[Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).
Pick a trigger bus (e.g. SFX); the plugin attenuates the carrier bus it's
attached to (e.g. Music) when the trigger crosses a threshold, then
releases.

## Install

**Paid plugin.** The source is open here for reference, but the pre-built
binary is distributed pay-what-you-want on itch.io:

**→ https://dsand64.itch.io/gmsb-sampler-ducking**

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart when prompted, then enable it under **Settings → Plugins**.

## Usage

Attach to the **Master** tier or any individual **Bus** in the FX Chain
editor. Open the plugin's editor and pick a trigger source from the
"Sidechain trigger" dropdown — typically a different bus than the one
you're ducking. Leave the dropdown on "(Self — no sidechain)" for
single-bus transient-tamer behaviour.

Knobs:

| Knob | Range | Default |
|------|-------|---------|
| Threshold | −60 to 0 dB | −20 dB |
| Duck depth | −30 to 0 dB | −12 dB |
| Attack | 1 to 200 ms | 20 ms |
| Release | 50 to 2000 ms | 500 ms |

## Manifest

| Field     | Value                       |
|-----------|-----------------------------|
| publisher | `github.DevinSanders`       |
| id        | `sampler.ducking`           |
| entryDll  | `DuckingPlugin.dll`         |

## Bypass behavior

This plugin holds DSP state (envelope follower). The host's BypassableSamplerInstance wrapper toggles bypass by flipping a flag — it does NOT rebuild the chain. While bypassed the wet path isn't clocked, so on un-bypass you'll briefly hear stale material from the moment bypass engaged. For an interactive soundboard with on-demand FX this is fine; for a long-tail stateful effect (large hall reverb, slow chorus) the artifact is more noticeable. Workaround: leave bypass off and use the wet/dry knob (or the plugin's own dry/wet mix where exposed) to mute the effect contribution.

## License

Released under the [MIT License](LICENSE).

Third-party components used by this plugin:

- No third-party DSP — original implementation. Uses the host's ISidechainSource SDK to listen on a trigger bus.