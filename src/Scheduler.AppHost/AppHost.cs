// Local development: PostgreSQL and Mailpit in containers, the web app as a project.
// Mailpit's UI (linked from the Aspire dashboard) shows every email the app "sends".
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithPgAdmin();

var schedulerDb = postgres.AddDatabase("schedulerdb");

var mailpit = builder.AddMailPit("mailpit");

builder.AddProject<Projects.Scheduler_Web>("web")
    .WithReference(schedulerDb)
    .WaitFor(schedulerDb)
    .WithReference(mailpit)
    .WaitFor(mailpit)
    .WithExternalHttpEndpoints();

builder.Build().Run();
