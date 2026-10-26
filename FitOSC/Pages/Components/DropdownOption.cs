namespace FitOSC.Pages.Components;

public sealed record DropdownOption<TValue>(TValue Value, string Label, bool Disabled = false);
