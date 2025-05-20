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
    // Другие узлы - это числа от 1 до 5, не включая свой номер
    options.Peers = new List<int> {1, 2, 3, 4, 5}.Except([options.Id]).ToList(); 
});
services.AddSingleton<RaftService>();

services.AddQuartz(q =>
{
    var jobKey = new JobKey("Elections");

    q.AddJob<ElectionJob>(opts => opts.WithIdentity(jobKey));

    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("Elections-trigger")
        .WithSimpleSchedule(x => x
            .WithIntervalInSeconds(4)
            .RepeatForever()
            .Build())
    );
});

services.AddQuartz(q =>
{
    var jobKey = new JobKey("Heartbeat");
        q.AddJob<HeartbeatJob>(opts => opts.WithIdentity(jobKey));
    
        q.AddTrigger(opts => opts
            .ForJob(jobKey)
            .WithIdentity("Heartbeat-trigger")
            .WithSimpleSchedule(x => x.
                WithIntervalInSeconds(3)
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