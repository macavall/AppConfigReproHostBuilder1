using Microsoft.Azure.AppConfiguration.Functions.Worker;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AppConfDurRepSim2
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureAppConfiguration(builder =>
                {
                    builder.AddAzureAppConfiguration(options =>
                    {
                        options.Connect(Environment.GetEnvironmentVariable("AZURE_APPCONFIG_CONNECTION_STRING"))
                               .ConfigureRefresh(refreshOptions =>
                               {
                                   refreshOptions.Register("TestApp", refreshAll: true);
                                   refreshOptions.SetRefreshInterval(TimeSpan.FromSeconds(1));
                               });
                    });
                })
                .ConfigureServices(s =>
                {
                    s.AddAzureAppConfiguration();
                    s.ConfigureFunctionsApplicationInsights();
                    s.AddApplicationInsightsTelemetryWorkerService();

                    s.Configure<LoggerFilterOptions>(options =>
                    {
                        // The Application Insights SDK adds a default logging filter that instructs ILogger to capture only Warning and more severe logs. Application Insights requires an explicit override.
                        // Log levels can also be configured using appsettings.json. For more information, see https://learn.microsoft.com/en-us/azure/azure-monitor/app/worker-service#ilogger-logs
                        LoggerFilterRule? toRemove = options.Rules.FirstOrDefault(rule => rule.ProviderName
                            == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");

                        if (toRemove is not null)
                        {
                            options.Rules.Remove(toRemove);
                        }
                    });
                })
                .ConfigureFunctionsWorkerDefaults(app =>
                {
                    app.UseAzureAppConfiguration();
                })

                .Build();

            host.Run();


            //var builder = FunctionsApplication.CreateBuilder(args);

            //builder.ConfigureFunctionsWebApplication();

            //builder.Services
            //    .AddApplicationInsightsTelemetryWorkerService()
            //    .ConfigureFunctionsApplicationInsights();

            //builder.Build().Run();
        }
    }

    public static class AzureAppConfigurationRefreshExtensions2
    {
        /// <summary>
        /// Configures a middleware for Azure App Configuration to use activity-based refresh for data configured in the provider.
        /// </summary>
        /// <param name="builder">An instance of <see cref="IFunctionsWorkerApplicationBuilder"/></param>
        public static IFunctionsWorkerApplicationBuilder UseAzureAppConfiguration(this IFunctionsWorkerApplicationBuilder builder)
        {
            IServiceProvider serviceProvider = builder.Services.BuildServiceProvider();
            IConfigurationRefresherProvider refresherProvider = serviceProvider.GetService<IConfigurationRefresherProvider>();

            // Verify if AddAzureAppConfiguration was done before calling UseAzureAppConfiguration.
            // We use the IConfigurationRefresherProvider to make sure if the required services were added.
            if (refresherProvider == null)
            {
                throw new InvalidOperationException($"Unable to find the required services. Please add all the required services by calling '{nameof(IServiceCollection)}.{nameof(AzureAppConfigurationExtensions.AddAzureAppConfiguration)}()' in the application startup code.");
            }

            if (refresherProvider.Refreshers?.Count() > 0)
            {
                //Microsoft.Azure.AppConfiguration.Functions
                //builder.UseMiddleware<AzureAppConfigurationRefreshMiddleware2>();

                builder.UseWhen<AzureAppConfigurationRefreshMiddleware2>(context => !context.FunctionDefinition.Parameters.Any(x => x.Type.FullName == "Microsoft.DurableTask.TaskOrchestrationContext"));
            }

            return builder;
        }
    }

    /// <summary>
    /// Middleware for Azure App Configuration to use activity-based refresh for key-values registered in the provider.
    /// </summary>
    internal class AzureAppConfigurationRefreshMiddleware2 : IFunctionsWorkerMiddleware
    {
        // The minimum refresh interval on the configuration provider is 1 second, so refreshing more often is unnecessary
        private static readonly long MinimumRefreshInterval = TimeSpan.FromSeconds(1).Ticks;
        private long _refreshReadyTime = DateTimeOffset.UtcNow.Ticks;

        private IEnumerable<IConfigurationRefresher> Refreshers { get; }

        private ILogger<AzureAppConfigurationRefreshMiddleware2> _logger;

        public AzureAppConfigurationRefreshMiddleware2(IConfigurationRefresherProvider refresherProvider, ILogger<AzureAppConfigurationRefreshMiddleware2> logger)
        {
            Refreshers = refresherProvider.Refreshers;
            _logger = logger;
        }

        public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
        {
            long utcNow = DateTimeOffset.UtcNow.Ticks;

            long refreshReadyTime = Interlocked.Read(ref _refreshReadyTime);

            // For logging purposes, you can remove the comment below to see logs writing the Full Name of the
            // FunctionDefinition Array Types

            //foreach (var item in context.FunctionDefinition.Parameters)
            //{
            //    _logger.LogInformation($"Parameter: {item.Type.FullName}");
            //}

            if (refreshReadyTime <= utcNow &&
                Interlocked.CompareExchange(ref _refreshReadyTime, utcNow + MinimumRefreshInterval, refreshReadyTime) == refreshReadyTime)
            {
                //
                // Configuration refresh is meant to execute as an isolated background task.
                // To prevent access of request-based resources, such as HttpContext, we suppress the execution context within the refresh operation.
                using (AsyncFlowControl flowControl = ExecutionContext.SuppressFlow())
                {
                    foreach (IConfigurationRefresher refresher in Refreshers)
                    {
                        _ = Task.Run(() => refresher.TryRefreshAsync());
                    }
                }
            }

            await next(context).ConfigureAwait(false);
        }
    }
}