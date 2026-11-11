using FitOSC.Services.State;

namespace FitOSC.Pages.Components;

/// <summary>
/// Lights an on-screen control briefly when it is pressed, on screen or from SteamVR. Disabled controls stay unlit (see CSS).
/// </summary>
public sealed class ControlGlow : IDisposable
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(600);

    private readonly AppStateService _appState;
    private readonly Func<Func<Task>, Task> _invoke;
    private readonly Action _render;
    private readonly HashSet<RemoteControl> _watched;
    // Each press bumps the control's generation, so only the latest press turns it off.
    private readonly Dictionary<RemoteControl, int> _lit = new();

    /// <param name="invoke">The component's InvokeAsync, so lighting runs on its render context.</param>
    /// <param name="render">The component's StateHasChanged.</param>
    public ControlGlow(AppStateService appState, Func<Func<Task>, Task> invoke, Action render, params RemoteControl[] controls)
    {
        _appState = appState;
        _invoke = invoke;
        _render = render;
        _watched = [.. controls];
        _appState.ControlPressed += OnControlPressed;
    }

    public string ClassFor(RemoteControl control) => _lit.ContainsKey(control) ? "control-glow" : string.Empty;

    private void OnControlPressed(RemoteControl control, bool acts)
    {
        if (!_watched.Contains(control))
            return;

        _ = _invoke(async () =>
        {
            var generation = _lit.GetValueOrDefault(control) + 1;
            _lit[control] = generation;
            _render();

            await Task.Delay(Duration);
            if (_lit.GetValueOrDefault(control) != generation)
                return;
            _lit.Remove(control);
            _render();
        });
    }

    public void Dispose() => _appState.ControlPressed -= OnControlPressed;
}
