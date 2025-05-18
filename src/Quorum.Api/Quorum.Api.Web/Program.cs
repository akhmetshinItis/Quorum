using Quartz;
using Quorum.Api.Core;
using Quorum.Web.Infrastructure;
using Quorum.Web.Services;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;

services.AddControllers();
services.AddSwaggerGen();

services.Configure<RaftOptions>(options =>
{
    options.Id = int.Parse(args[0]);
    options.IsLeader = bool.Parse(args[1]);
    options.AllClusterNodeIds = args.Skip(2).Select(int.Parse).ToList();
    options.Followers = options.AllClusterNodeIds.Where(id => id != options.Id).ToList();
});
services.AddSingleton<ILoggingService, LoggingService>();
services.AddSingleton<RaftService>();

services.AddQuartz(q =>
{
    var jobKey = new JobKey("Heartbeat");
    q.AddJob<HeartbeatJob>(opts => opts.WithIdentity(jobKey));
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("Heartbeat-trigger")
        .WithSimpleSchedule(x => x
            .WithIntervalInSeconds(1)
            .RepeatForever()
            .Build())
    );
});
services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

services.AddHttpClient();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.UseHttpsRedirection();

app.Run();