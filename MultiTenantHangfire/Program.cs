using Hangfire;
using Hangfire.Client;
using Hangfire.Common;
using Hangfire.MemoryStorage;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {        
        services.AddScoped<IUser, User>();
        services.AddScoped<ITenantProvider, PerformContextTenantProvider>();
        services.AddScoped<IHangfireContext, HangfireContext>();
        services.AddSingleton<JobActivator, CustomJobActivator>();

        services.AddTransient<IBackgroundJobClient>(provider => new BackgroundJobClient(provider.GetService<JobStorage>(),
            new TenantWriterFilterProvider(provider.GetService<ITenantProvider>()!)));

        services.AddHangfire((provider, configuration) =>
        {
            configuration.UseActivator(provider.GetService<JobActivator>()!);
            configuration.UseMemoryStorage();
        });

        services.AddHangfireServer((provider, options) =>
        {
            // options.FilterProvider = new TenantReaderFilterProvider(provider.GetService<ITenantProvider>()!);
        });
    })
    .Build();

var background = host.Services.GetService<IBackgroundJobClient>();
background.Enqueue<TestJob>(x => x.Run());

await host.RunAsync();

public interface ITenantProvider
{
    int TenantId { get; }

    void SetTenant(int tenantId);
}

// A provider that extracts the tenant from a claim, domain etc
public class StubTenantProvider : ITenantProvider
{
    public int TenantId { get; private set; } = 1;
    
    public void SetTenant(int tenantId)
    {
        TenantId = tenantId;
    }
}

public class PerformContextTenantProvider : ITenantProvider
{
    private readonly PerformContext _performContext;
    
    public PerformContextTenantProvider(IHangfireContext hangfireContext)
    {
        TenantId = hangfireContext.JobActivatorContext?.GetJobParameter<int>("TenantId") ?? 0;
    }
    
    public int TenantId { get; }
    
    public void SetTenant(int tenantId)
    {
        throw new NotImplementedException();
    }
}

public class TestJob
{
    private readonly ILogger<TestJob> _logger;
    private readonly IUser _user;
    private readonly ITenantProvider _tenantProvider;

    public TestJob(ILogger<TestJob> logger, IUser user, ITenantProvider tenantProvider)
    {
        _logger = logger;
        _user = user;
        _tenantProvider = tenantProvider;
    }

    public void Run()
    {
        _logger.LogInformation("Job run for id {job} job name {name}", _user.Id, _user.Name);
        _logger.LogInformation("Tenant ID for this scope is: {tenantId}", _tenantProvider.TenantId);
    }
}

public interface IUser
{
    public string Id { get; }
    public string Name { get; }
}

public class User : IUser
{
    public User(IHangfireContext hangfireContext)
    {
        Id = hangfireContext.JobActivatorContext?.BackgroundJob.Id ?? "-";
        Name = hangfireContext.JobActivatorContext?.BackgroundJob.Job.Type.Name ?? "-";
    }

    public string Id { get; }

    public string Name { get; }
}

public class CustomJobActivator : JobActivator
{
    private readonly IServiceProvider _serviceProvider;
    private readonly PerformContext _performContext;

    public CustomJobActivator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public override object ActivateJob(Type jobType)
    {
        return _serviceProvider.GetService(jobType)!;
    }

    public override JobActivatorScope BeginScope(JobActivatorContext context)
    {
        var scope = _serviceProvider.CreateScope();
        return new CustomScope(scope, context);
    }
}

public class CustomScope : JobActivatorScope
{
    private readonly IServiceScope _serviceScope;

    public CustomScope(IServiceScope serviceScope, JobActivatorContext context)
    {
        _serviceScope = serviceScope;

        var hangfireContext = _serviceScope.ServiceProvider.GetRequiredService<IHangfireContext>();
        hangfireContext.JobActivatorContext = context;
    }

    public override object Resolve(Type type)
    {
        return ActivatorUtilities.CreateInstance(_serviceScope.ServiceProvider, type);
    }

    public override void DisposeScope()
    {
        _serviceScope.Dispose();
    }
}

public class HangfireContext : IHangfireContext
{
    public JobActivatorContext? JobActivatorContext { get; set; }
    
    public PerformContext PerformContext { get; set; }
}

public interface IHangfireContext
{
    JobActivatorContext? JobActivatorContext { get; set; }
    
    PerformContext PerformContext { get; set; }
}

public class TenantWriterFilter : IClientFilter
{
    // private readonly ITenantProvider _tenantProvider;
    //
    // public TenantWriterFilter(ITenantProvider tenantProvider)
    // {
    //     _tenantProvider = tenantProvider;
    // }

    public void OnCreating(CreatingContext filterContext)
    {
        filterContext.SetJobParameter("TenantId", 100);
    }

    public void OnCreated(CreatedContext filterContext)
    {
    }
}

// public class TenantReaderFilter : IServerFilter
// {
//     private readonly ITenantProvider _tenantProvider;
//     private readonly IServiceScope _serviceScope;
//
//     public TenantReaderFilter(ITenantProvider tenantProvider, IServiceScope serviceScope)
//     {
//         _tenantProvider = tenantProvider;
//         _serviceScope = serviceScope;
//     }
//
//     public void OnPerforming(PerformingContext filterContext)
//     {
//         var tenantId = filterContext.GetJobParameter<int>("TenantId");
//         // _tenantProvider.SetTenant(tenantId);
//         
//         // hardcode the tenant ID to assert proper injection for a given scope
//         _tenantProvider.SetTenant(100);
//     }
//
//     public void OnPerformed(PerformedContext filterContext)
//     {
//     }
// }
//
// public class TenantReaderFilterProvider : IJobFilterProvider
// {
//     // private readonly ITenantProvider _tenantProvider;
//     // private readonly IServiceScope _serviceScope;
//     //
//     // public TenantReaderFilterProvider(ITenantProvider tenantProvider)
//     // {
//     //     _tenantProvider = tenantProvider;
//     //     // _serviceScope = serviceScope;
//     // }
//
//     public IEnumerable<JobFilter> GetFilters(Job job)
//     {
//         return new JobFilter[]
//         {
//             new JobFilter(new TenantReaderFilter(), JobFilterScope.Global,  null),
//         };
//     }
// }

public class TenantWriterFilterProvider : IJobFilterProvider
{
    private readonly ITenantProvider _tenantProvider;

    public TenantWriterFilterProvider(ITenantProvider tenantProvider)
    {
        _tenantProvider = tenantProvider;
    }

    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        return new JobFilter[]
        {
            new(new TenantWriterFilter(), JobFilterScope.Global,  null),
        };
    }
}