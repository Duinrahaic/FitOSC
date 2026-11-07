// Feedback chimes for control presses: two notes up when a press acts, two notes down when it can't.
// Gain of each note at full volume; the user's volume (0 to 1) scales it.
const maxGain = 0.14;
let context;

function note(volume, hz, delay, seconds) {
    const start = context.currentTime + delay;
    const oscillator = new OscillatorNode(context, { type: 'sine', frequency: hz });

    // A fast attack and an exponential release keep the note free of clicks.
    const gain = new GainNode(context, { gain: 0 });
    gain.gain.setValueAtTime(0, start);
    gain.gain.linearRampToValueAtTime(maxGain * volume, start + 0.004);
    gain.gain.exponentialRampToValueAtTime(0.0001, start + seconds);

    oscillator.connect(gain).connect(context.destination);
    oscillator.start(start);
    oscillator.stop(start + seconds);
}

function chime(volume, notes) {
    if (volume <= 0) return;
    context ??= new AudioContext();
    if (context.state === 'suspended') context.resume();
    for (const [hz, delay, seconds] of notes)
        note(volume, hz, delay, seconds);
}

// G5 then D6.
export const chimeUp = volume => chime(volume, [[784, 0, 0.07], [1175, 0.035, 0.1]]);

// C5 then F#4.
export const chimeDown = volume => chime(volume, [[523, 0, 0.07], [370, 0.045, 0.12]]);
