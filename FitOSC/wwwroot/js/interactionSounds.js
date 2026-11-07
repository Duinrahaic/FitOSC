// Treadmill-style feedback beeps for control presses: high when a press acts, low when it can't.
// Gain at full volume; the user's volume (0 to 1) scales it.
const maxGain = 0.08;
let context;

// A short square-wave tone, filtered to soften it into the sound of a console's piezo buzzer.
function beep(volume, hz, seconds) {
    if (volume <= 0) return;
    context ??= new AudioContext();
    if (context.state === 'suspended') context.resume();

    const now = context.currentTime;
    const oscillator = new OscillatorNode(context, { type: 'square', frequency: hz });
    const filter = new BiquadFilterNode(context, { type: 'lowpass', frequency: hz * 2.5 });

    // Flat with a fast attack and release, like a buzzer switching on and off, without clicks.
    const gain = new GainNode(context, { gain: 0 });
    gain.gain.setValueAtTime(0, now);
    gain.gain.linearRampToValueAtTime(maxGain * volume, now + 0.003);
    gain.gain.setValueAtTime(maxGain * volume, now + seconds - 0.008);
    gain.gain.linearRampToValueAtTime(0, now + seconds);

    oscillator.connect(filter).connect(gain).connect(context.destination);
    oscillator.start(now);
    oscillator.stop(now + seconds);
}

export const beepHigh = volume => beep(volume, 2400, 0.09);

export const beepLow = volume => beep(volume, 800, 0.14);
