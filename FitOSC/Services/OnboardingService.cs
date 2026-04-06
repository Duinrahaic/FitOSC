namespace FitOSC.Services;

/// <summary>
/// Service to manage onboarding wizard state and triggering
/// </summary>
public class OnboardingService
{
    private readonly ILogger<OnboardingService> _logger;

    public event Action? OnShowOnboarding;

    public OnboardingService(ILogger<OnboardingService> logger)
    {
        _logger = logger;
    }

    public void TriggerOnboarding()
    {
        _logger.LogInformation("Onboarding wizard triggered");
        OnShowOnboarding?.Invoke();
    }
}
