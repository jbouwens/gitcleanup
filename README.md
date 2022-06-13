### Git Cleanup

Removes merged branches. Can be automated with Windows Task Scheduler, and integrated with a slack web hook.

![this is how it looks in slack](/slack.png)

### What is a merged branch?

Bitbucket determines this by the ahead information of a branch. If ahead=0, it is merged.
Bitbucket doesn't currently make this available in their API, so libgit2sharp is used to determine this.


### How do I build it?
Open a command prompt or PowerShell window and run: `dotnet build --configuration Release`

Otherwise, open the Solution file in Visual Studio and Build.

The binary will be in GitCleanup\bin\Release\netcoreapp3.1