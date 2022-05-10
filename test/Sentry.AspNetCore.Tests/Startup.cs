using Microsoft.AspNetCore.Builder;
#if NETCOREAPP2_1 || NET461
using IHostingEnvironment = Microsoft.Extensions.Hosting.IHostingEnvironment;
#else
using IHostingEnvironment = Microsoft.Extensions.Hosting.IHostEnvironment;
#endif
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Sentry.AspNetCore.Tests;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Configure(IApplicationBuilder app, IHostingEnvironment env)
    {
    }
}
