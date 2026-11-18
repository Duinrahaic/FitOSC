// Feedback beeps for control presses: a warm beep when a press acts, the same beep a fifth lower when it can't.
// Gain at full volume; the user's volume (0 to 1) scales it.
const maxGain = 0.24;
let context;

// A sine with a faint octave overtone for body, low-passed to keep it round.
function beep(volume, hz, seconds) {
    if (volume <= 0) return;
    context ??= new AudioContext();
    if (context.state === 'suspended') context.resume();

    const now = context.currentTime;
    const peak = maxGain * volume;

    // A soft attack, a short hold, then an exponential fade keep the beep smooth and free of clicks.
    const gain = new GainNode(context, { gain: 0 });
    gain.gain.setValueAtTime(0, now);
    gain.gain.linearRampToValueAtTime(peak, now + 0.01);
    gain.gain.setValueAtTime(peak, now + seconds * 0.45);
    gain.gain.exponentialRampToValueAtTime(0.0001, now + seconds);
    gain.connect(new BiquadFilterNode(context, { type: 'lowpass', frequency: hz * 2.2, Q: 0.5 })).connect(context.destination);

    for (const [multiple, level] of [[1, 1 / 1.2], [2, 0.2 / 1.2]]) {
        const oscillator = new OscillatorNode(context, { type: 'sine', frequency: hz * multiple });
        oscillator.connect(new GainNode(context, { gain: level })).connect(gain);
        oscillator.start(now);
        oscillator.stop(now + seconds);
    }
}

export const beepHigh = volume => beep(volume, 620, 0.15);

// The can't-act beep is a little longer and louder so it reads as clearly as the high one.
export const beepLow = volume => beep(volume * 1.125, 415, 0.2);
