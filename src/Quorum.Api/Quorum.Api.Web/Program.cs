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
});
services.AddSingleton<RaftService>();

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