### Git Cleanup

Removes merged branches. Can be automated with Windows Task Scheduler.

### What is a merged branch?

Bitbucket determines this by the ahead information of a branch. If ahead=0, it is merged.

![Bitbucket interface for displaying merged branches](/ahead.png)

{bitbucket repo}/branches/?status=merged
![API call attempt](/ahead-api.png)

Bitbucket doesn't currently make this available in their API, so libgit2sharp is used.


### How do I build it?
Open a command prompt or PowerShell window and run: `dotnet build --configuration Release`

Otherwise, open the Solution file in Visual Studio and Build.

The binary will be in GitCleanup\bin\Release\netcoreapp3.1