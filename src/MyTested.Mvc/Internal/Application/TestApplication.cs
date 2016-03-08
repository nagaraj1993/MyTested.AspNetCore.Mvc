﻿namespace MyTested.Mvc.Internal.Application
{
    using System;
    using System.Threading.Tasks;
    using Caching;
    using Contracts;
    using Logging;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Hosting.Internal;
    using Microsoft.AspNetCore.Hosting.Startup;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Routing;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.DependencyInjection.Extensions;
    using Microsoft.Extensions.Logging;
    using Microsoft.AspNetCore.Mvc.Abstractions;
    using Routes;
    using System.Linq;
    using System.Diagnostics;
    using Microsoft.AspNetCore.Mvc.Internal;
    using Microsoft.AspNetCore.Mvc;
    using Formatters;
    using Microsoft.AspNetCore.Mvc.Formatters;
    using Microsoft.Extensions.PlatformAbstractions;
    using System.Reflection;
    using Microsoft.AspNetCore.Hosting;
    using Microsoft.AspNetCore.Http.Internal;
    using Microsoft.Extensions.Configuration;
    using Microsoft.AspNetCore.Mvc.ViewFeatures;
    using Microsoft.Extensions.Caching.Memory;

    public static class TestApplication
    {
        private static readonly RequestDelegate NullHandler = (c) => Task.FromResult(0);
        private static readonly IHostingEnvironment Environment = new HostingEnvironment { EnvironmentName = "Tests" };

        private static bool initialiazed;
        private static IConfigurationBuilder configurationBuilder;
        private static IConfiguration configuration;
        private static Type startupType;

        private static IServiceProvider serviceProvider;
        private static IServiceProvider routeServiceProvider;
        private static IRouter router;

        static TestApplication()
        {
            configuration = PrepareConfiguration();
        }

        internal static Type StartupType
        {
            get
            {
                return startupType;
            }
            set
            {
                Reset();
                startupType = value;
            }
        }

        internal static Action<IServiceCollection> AdditionalServices { get; set; }

        internal static Action<IApplicationBuilder> AdditionalApplicationConfiguration { get; set; }

        internal static Action<IRouteBuilder> AdditionalRoutes { get; set; }

        public static IServiceProvider Services
        {
            get
            {
                if (!initialiazed)
                {
                    Initialize();
                }

                return serviceProvider;
            }
        }

        public static IServiceProvider RouteServices
        {
            get
            {
                if (!initialiazed)
                {
                    Initialize();
                }

                return routeServiceProvider;
            }
        }

        public static IRouter Router
        {
            get
            {
                if (!initialiazed)
                {
                    Initialize();
                }

                return router;
            }
        }

        internal static void TryFindDefaultStartupType()
        {
            var applicationName = PlatformServices.Default.Application.ApplicationName;
            var applicationAssembly = Assembly.Load(new AssemblyName(applicationName));

            var startupName = $"{Environment.EnvironmentName}Startup";

            // check root of the testing library
            startupType = 
                applicationAssembly.GetType(startupName) ??
                applicationAssembly.GetType($"{applicationName}.{startupName}");

            if (startupType == null)
            {
                // full scan 
                var startupTypes = applicationAssembly
                    .DefinedTypes
                    .Where(t =>
                    {
                        return t.Name == startupName || t.Name == $"{applicationName}.{startupName}";
                    })
                    .Select(t => t.AsType())
                    .ToArray();

                if (startupTypes.Length == 1)
                {
                    startupType = startupTypes.First();
                }
            }
        }

        private static IConfiguration PrepareConfiguration()
        {
            return new ConfigurationBuilder()
                .AddInMemoryCollection()
                .Build();
        }

        private static void Initialize()
        {
            var serviceCollection = GetInitialServiceCollection();

            StartupMethods startupMethods = null;
            if (StartupType != null)
            {
                startupMethods = serviceCollection
                    .BuildServiceProvider()
                    .GetRequiredService<IStartupLoader>()
                    .LoadMethods(StartupType, null);
            }

            PrepareServices(serviceCollection, startupMethods);
            PrepareApplicationAndRoutes(startupMethods);

            initialiazed = true;
        }

        private static IServiceCollection GetInitialServiceCollection()
        {
            var serviceCollection = new ServiceCollection();
            var diagnosticSource = new DiagnosticListener("MyTested.Mvc");

            // default server services
            serviceCollection.TryAddSingleton(Environment);
            serviceCollection.TryAddSingleton<ILoggerFactory>(MockedLoggerFactory.Create());

            serviceCollection.AddTransient<IStartupLoader, StartupLoader>();

            serviceCollection.TryAddTransient<IHttpContextFactory, HttpContextFactory>();
            serviceCollection.AddLogging();
            serviceCollection.AddOptions();

            serviceCollection.TryAddSingleton<DiagnosticSource>(diagnosticSource);
            serviceCollection.TryAddSingleton(diagnosticSource);

            // platform services
            AddPlatformServices(serviceCollection);

            // testing framework services
            serviceCollection.TryAddSingleton<IControllerActionDescriptorCache, ControllerActionDescriptorCache>();

            // custom MVC options
            serviceCollection.Configure<MvcOptions>(options =>
            {
                // string input formatter helps with HTTP request processing
                var inputFormatters = options.InputFormatters.OfType<TextInputFormatter>();
                if (!inputFormatters.Any(f => f.SupportedMediaTypes.Contains(ContentType.TextPlain)))
                {
                    options.InputFormatters.Add(new StringInputFormatter());
                }
            });

            return serviceCollection;
        }

        private static void PrepareServices(IServiceCollection serviceCollection, StartupMethods startupMethods)
        {
            if (startupMethods?.ConfigureServicesDelegate != null)
            {
                startupMethods.ConfigureServicesDelegate(serviceCollection);
            }
            else
            {
                serviceCollection.AddMvc();
            }

            if (AdditionalServices != null)
            {
                AdditionalServices(serviceCollection);
            }

            PrepareRouteServices(serviceCollection);

            serviceCollection.TryReplaceSingleton<ITempDataProvider, MockedTempDataProvider>();

            serviceCollection.TryRemoveSingleton<IMemoryCache, MemoryCache>();
            serviceCollection.TryAddTransient<IMemoryCache, MockedMemoryCache>();

            serviceProvider = serviceCollection.BuildServiceProvider();
        }

        private static void PrepareRouteServices(IServiceCollection serviceCollection)
        {
            var modelBindingActionInvokerFactoryType = typeof(IModelBindingActionInvokerFactory);

            if (!serviceCollection.Any(s => s.ServiceType == modelBindingActionInvokerFactoryType))
            {
                serviceCollection.TryAddEnumerable(
                    ServiceDescriptor.Transient<IActionInvokerProvider, ModelBindingActionInvokerProvider>());
                serviceCollection.TryAddSingleton(modelBindingActionInvokerFactoryType, typeof(ModelBindingActionInvokerFactory));
            }

            routeServiceProvider = serviceCollection.BuildServiceProvider();

            serviceCollection.TryRemoveSingleton(modelBindingActionInvokerFactoryType);

            var actionInvokerProviders = serviceCollection.Where(s => s.ServiceType == typeof(IActionInvokerProvider)).ToList();
            if (actionInvokerProviders.Count > 1)
            {
                serviceCollection.Remove(actionInvokerProviders.LastOrDefault());
            }
        }

        private static void PrepareApplicationAndRoutes(StartupMethods startupMethods)
        {
            var applicationBuilder = new MockedApplicationBuilder(serviceProvider);

            if (startupMethods?.ConfigureDelegate != null)
            {
                startupMethods.ConfigureDelegate(applicationBuilder);
            }

            if (AdditionalApplicationConfiguration != null)
            {
                AdditionalApplicationConfiguration(applicationBuilder);
            }

            var routeBuilder = new RouteBuilder(applicationBuilder)
            {
                DefaultHandler = new RouteHandler(NullHandler)
            };

            for (int i = 0; i < applicationBuilder.Routes.Count; i++)
            {
                var route = applicationBuilder.Routes[i];
                routeBuilder.Routes.Add(route);
            }

            if (AdditionalRoutes != null)
            {
                AdditionalRoutes(routeBuilder);
            }

            if (StartupType == null || routeBuilder.Routes.Count == 0)
            {
                routeBuilder.MapRoute(
                    name: "default",
                    template: "{controller=Home}/{action=Index}/{id?}");

                routeBuilder.Routes.Insert(0, AttributeRouting.CreateAttributeMegaRoute(
                    routeBuilder.DefaultHandler,
                    serviceProvider));
            }

            router = routeBuilder.Build();
        }
        
        private static void AddPlatformServices(IServiceCollection serviceCollection)
        {
            var defaultPlatformServices = PlatformServices.Default;
            if (defaultPlatformServices != null)
            {
                if (defaultPlatformServices.Application != null)
                {
                    var appEnv = defaultPlatformServices.Application;
                    serviceCollection.TryAddSingleton(appEnv);
                }

                if (defaultPlatformServices.Runtime != null)
                {
                    serviceCollection.TryAddSingleton(defaultPlatformServices.Runtime);
                }
            }
        }

        private static void Reset()
        {
            initialiazed = false;
            startupType = null;
            serviceProvider = null;
            routeServiceProvider = null;
            router = null;
            AdditionalServices = null;
            AdditionalApplicationConfiguration = null;
            AdditionalRoutes = null;
        }
    }
}
