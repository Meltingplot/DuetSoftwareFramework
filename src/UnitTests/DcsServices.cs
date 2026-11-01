using System;
using System.Reflection;
using DuetControlServer;
using DuetControlServer.Codes;
using DuetControlServer.Commands;
using DuetControlServer.Files;
using DuetControlServer.Link;
using DuetControlServer.Link.Adapter;
using DuetControlServer.Link.Protocol.Shared;
using DuetControlServer.Model;
using DuetControlServer.Utility;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace UnitTests;

/// <summary>
/// DCS services for fork tests, wired like in DCS
/// </summary>
public static class DcsServices
{
    /// <summary>
    /// Build the services with a link adapter that does nothing
    /// </summary>
    /// <param name="settings">Settings or null for the defaults</param>
    /// <returns>Service provider</returns>
    public static ServiceProvider Build(Settings? settings = null) => Build(settings ?? new Settings(), DispatchProxy.Create<ILinkAdapter, NullLinkAdapter>());

    /// <summary>
    /// Build the services
    /// </summary>
    /// <param name="settings">Settings</param>
    /// <param name="linkAdapter">Link adapter</param>
    /// <param name="configure">Optional callback to add more services</param>
    /// <returns>Service provider</returns>
    public static ServiceProvider Build(Settings settings, ILinkAdapter linkAdapter, Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services
            .AddLogging()
            .AddSingleton(Options.Create(settings))
            .AddSingleton<IHostApplicationLifetime, ApplicationLifetime>()
            .AddSingleton(linkAdapter)
            .AddSingleton<LinkInterface>()
            .AddCodes()
            .AddCommands()
            .AddFiles()
            .AddLink()
            .AddModel()
            .AddUtility();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Link adapter that does nothing, tests feed replies and requests to the services directly
    /// </summary>
    public class NullLinkAdapter : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == $"get_{nameof(ILinkAdapter.ProtocolVersion)}")
            {
                return (int)Consts.ProtocolVersion;
            }
            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void) ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}
