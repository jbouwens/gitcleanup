using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CredentialManagement;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using Slack.Webhooks;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace GitCleanup
{
    internal class Program
    {
        private static readonly IConfigurationRoot Config = ConfigHelper.BuildConfiguration();
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private static void Main(string[] args)
        {
            Logger.Info("Starting application ...");

            try
            {
                var servicesProvider = BuildDi(Config);
                using (servicesProvider as IDisposable)
                {
                    CleanRepos();
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Stopped program because of exception");
                throw;
            }
            finally
            {
                Logger.Info("Shutting down application ...");
                LogManager.Shutdown();
            }
        }

        private static void CleanRepos()
        {
            var configuredRepositories = Config.GetSection("ConfiguredRepositories").Get<List<ConfiguredRepository>>();

            foreach (var configuredRepository in configuredRepositories)
            {
                CleanRepo(configuredRepository);
            }
        }


        private static void CleanRepo(ConfiguredRepository configuredRepository)
        {
            var credentials = GetCredentials();
            var pathToRepo = Path.Join(Config["PathToRepos"], configuredRepository.Name);
            var totalBranches = 0;
            var totalBranchesToDelete = 0;

            Directory.CreateDirectory(Config["PathToRepos"]);
            if (!Directory.Exists(pathToRepo))
            {
                CloneRepository(configuredRepository, credentials, pathToRepo);
            }

            using var repo = new Repository(pathToRepo);

            var masterBranch = repo.Branches[$"origin/{configuredRepository.MasterBranchName}"];

            if (masterBranch == null)
            {
                Logger.Warn($"Master branch \"{configuredRepository.MasterBranchName}\" not found.");
                return;
            }

            PerformFetch(repo, credentials);

            Logger.Info($"determining divergence from master @ {masterBranch.Tip.Sha.Substring(0, 7)} for {configuredRepository.Name}");

            foreach (var branch in repo.Branches
                .Where(x => x.IsRemote && configuredRepository.IgnoredBranches != null && !configuredRepository.IgnoredBranches.Contains(x.FriendlyName)).OrderByDescending(s => s.Tip.Author.When))
            {
                totalBranches++;

                //A branch is "merged" if it's ahead by 0.
                var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, masterBranch.Tip);

                if (divergence.AheadBy == 0)
                {
                    totalBranchesToDelete++;
                    DeleteBranch(configuredRepository.Name, branch, repo, credentials);
                }
            }

            Logger.Info($"Checked {totalBranches} branches in {configuredRepository.Name}, found {totalBranchesToDelete} candidates.");
        }

        private static void PerformFetch(Repository repo, UserPass credentials)
        {
            var remote = repo.Network.Remotes["origin"];
            var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);

            var logMessage = string.Empty;
            try
            {
                Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions
                {
                    CredentialsProvider = (url, usernameFromUrl, types) =>
                        new UsernamePasswordCredentials {Username = credentials.Username, Password = credentials.Password},
                    Prune = true
                }, logMessage);
            }
            catch (Exception e)
            {
                throw new Exception("Error fetching, invalid credentials?", e);
            }
        }

        private static void DeleteBranch(string repoName, Branch branch, Repository repo, UserPass credentials)
        {
            var pushOptions = new PushOptions
            {
                OnNegotiationCompletedBeforePush = pushUpdates => Notify(pushUpdates, branch, repoName),
                OnPushStatusError = ErrorNotify,
                CredentialsProvider = (url, usernameFromUrl, types) =>
                    new UsernamePasswordCredentials {Username = credentials.Username, Password = credentials.Password}
            };

            if (Config.GetValue<bool>("PushDeletesToRemote"))
            {
                repo.Network.Push(repo.Network.Remotes["origin"], ":" + branch.UpstreamBranchCanonicalName, pushOptions);
            }
            else
            {
                Logger.Info($"{repoName} => {branch.FriendlyName} => {branch.Tip.Sha.Substring(0, 7)}");
            }
        }

        private static void ErrorNotify(PushStatusError pushStatusErrors)
        {
            Logger.Error($"Error Pushing. {pushStatusErrors.Message}");
        }

        private static bool Notify(IEnumerable<PushUpdate> pushUpdates, Branch branch, string repoName)
        {
            if (pushUpdates != null && pushUpdates.Any() && pushUpdates.FirstOrDefault().SourceObjectId.Sha == branch.Tip.Sha)
            {
                Logger.Info($"{repoName} => {branch.FriendlyName} => {branch.Tip.Sha.Substring(0, 7)}");
                var slackClient = new SlackClient(Config["SlackWebhook"]);
                var slackMessage = new SlackMessage
                {
                    Text = $"`{repoName} => {branch.FriendlyName} => {branch.Tip.Sha.Substring(0, 7)}`"
                };
                slackClient.Post(slackMessage);
                return true;
            }

            Logger.Error($"Delete failed for {repoName} => {branch.FriendlyName} => {branch.Tip.Sha.Substring(0, 7)}");
            return true;
        }

        private static void CloneRepository(ConfiguredRepository configuredRepository, UserPass credentials, string pathToRepo)
        {
            var options = new CloneOptions
            {
                CredentialsProvider = (url, user, cred) => new UsernamePasswordCredentials
                {
                    Username = credentials.Username,
                    Password = credentials.Password
                },
                IsBare = true
            };

            Logger.Info($"Cloning a {configuredRepository.Name} to {pathToRepo}, please wait.");
            Repository.Clone(configuredRepository.Url, pathToRepo, options);
            Logger.Info($"Cloning of {configuredRepository.Name} to {pathToRepo} complete.");
        }

        private static UserPass GetCredentials()
        {
            var cm = new Credential { Target = "gitcleanup" };
            if (!cm.Load())
            {
                return null;
            }

            return new UserPass
            {
                Username = cm.Username,
                Password = cm.Password
            };
        }

        private static IServiceProvider BuildDi(IConfiguration config)
        {
            return new ServiceCollection()
                .AddTransient<Runner>()
                .AddLogging(loggingBuilder =>
                {
                    loggingBuilder.ClearProviders();
                    loggingBuilder.SetMinimumLevel(LogLevel.Trace);
                    loggingBuilder.AddNLog(config);
                })
                .BuildServiceProvider();
        }

        public class ConfiguredRepository
        {
            public string Name { get; set; }
            public string Url { get; set; }
            public string MasterBranchName { get; set; }
            public List<string> IgnoredBranches { get; set; }
        }
    }

    public class UserPass
    {
        public string Username { get; set; }
        public string Password { get; set; }
    }
}