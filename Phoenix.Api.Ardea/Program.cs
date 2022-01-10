using Microsoft.EntityFrameworkCore;
using Phoenix.DataHandle.Main.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => 
{
    o.EnableAnnotations();

    // SwaggerDoc name refers to the name of the documention and is included in the endpoint path
    o.SwaggerDoc("v3", new Microsoft.OpenApi.Models.OpenApiInfo()
    {
        Title = "Ardea API",
        Description = "A Rest API to synchronize the data from WordPress with the Phoenix backend.",
        Version = "3.0"
    });
});

builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

string phoenixConnection = builder.Configuration.GetConnectionString("PhoenixConnection");
builder.Services.AddDbContext<PhoenixContext>(o => o.UseLazyLoadingProxies().UseSqlServer(phoenixConnection));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v3/swagger.json", "Ardea v3"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
