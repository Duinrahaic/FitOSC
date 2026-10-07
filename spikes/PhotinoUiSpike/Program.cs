using Photino.Blazor;
using PhotinoUiSpike;

var builder = PhotinoBlazorApp.CreateBuilder(args);
builder.RootComponents.Add<SpikePage>("#app");
builder.ConfigureMainWindow(window => window
    .SetTitle("FitOSC Phase 0 - PhotinoX UI spike")
    .SetSize(700, 800)
    .SetResizable(false)
    .SetDevToolsEnabled(true)
    .SetLogVerbosity(0));

await using var app = builder.Build();
return app.Run();
