using Quartz;
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
    options.Followers = new List<int>(args.Skip(2).Select(int.Parse));
});
services.AddSingleton<RaftService>();

services.AddQuartz(q =>
{
    if (bool.Parse(args[1]))
    {
        var jobKey = new JobKey("Heartbeat");
            q.AddJob<HeartbeatJob>(opts => opts.WithIdentity(jobKey));
        
            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity("Heartbeat-trigger")
                .WithSimpleSchedule(x => x.
                    WithIntervalInSeconds(15)
                    .RepeatForever()
                    .Build())
            );
    }
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