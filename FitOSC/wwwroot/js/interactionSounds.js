// Feedback tones for control presses: beep when a press acts, booewp when it can't.
// Gain at full volume; the user's volume (0 to 1) scales it.
const maxGain = 0.2;
let context;

function tone(volume, type, startHz, endHz, seconds) {
    if (volume <= 0) return;
    context ??= new AudioContext();
    if (context.state === 'suspended') context.resume();

    const now = context.currentTime;
    const oscillator = new OscillatorNode(context, { type });
    oscillator.frequency.setValueAtTime(startHz, now);
    oscillator.frequency.exponentialRampToValueAtTime(endHz, now + seconds);

    // A fast attack and an exponential release keep the tone free of clicks.
    const gain = new GainNode(context, { gain: 0 });
    gain.gain.setValueAtTime(0, now);
    gain.gain.linearRampToValueAtTime(maxGain * volume, now + 0.005);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + seconds);

    oscillator.connect(gain).connect(context.destination);
    oscillator.start(now);
    oscillator.stop(now + seconds);
}

export const beep = volume => tone(volume, 'sine', 1320, 1320, 0.06);

export const booewp = volume => tone(volume, 'triangle', 330, 150, 0.18);
