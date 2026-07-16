using Jaiz_POS_MSC_SettlementService;
using Jaiz_POS_MSC_SettlementService.Data;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();

// Global Exception Handlers
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var ex = e.ExceptionObject as Exception;

    Log.Fatal(ex, "UNHANDLED EXCEPTION. Service is terminating.");

    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Log.Fatal(e.Exception, "UNOBSERVED TASK EXCEPTION.");

    e.SetObserved();
};

AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
{
    Log.Warning("PROCESS EXIT detected.");

    Log.CloseAndFlush();
};

builder.Services.AddWindowsService();

builder.Services.AddHostedService<Worker>();

builder.Services.AddDbContext<AppDbContext>(options =>options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();
builder.Logging.AddEventLog();

try
{
    Log.Information("========================================");
    Log.Information("Settlement Service Starting");
    Log.Information("========================================");

    var host = builder.Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SERVICE TERMINATED UNEXPECTEDLY.");
}
finally
{
    Log.Information("Settlement Service Shutdown");

    Log.CloseAndFlush();
}