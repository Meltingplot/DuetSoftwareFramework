using System.Reflection;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace UnitTests;

/// <summary>
/// DCS services for fork tests, wired like in DCS but with a link adapter that does nothing
/// </summary>
public static class DcsServices
{
    /// <summary>
    /// Build the services
    /// </summary>
    /// <param name="settings">Settings or null for the defaults</param>
    /// <returns>Service provider</returns>
    public static ServiceProvider Build(Settings? settings = null)
    {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(settings ?? new Settings()))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(DispatchProxy.Create<ILinkAdapter, SPI.ChannelProcessor.NullLinkAdapter>())
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility();
        return services.BuildServiceProvider();
    }
}
