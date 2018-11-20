using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.Policy.WebApi;
using Microsoft.TeamFoundation.Work.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Client;
using Microsoft.VisualStudio.Services.WebApi;
using System.Text.RegularExpressions;

namespace AzureDevOpsScanner
{
    class Program
    {
        private readonly static Regex EmailRegex = new Regex(@"\A(?:[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)\Z", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        //============= Config [Edit these with your settings] =====================
        internal const string azureDevOpsOrganizationUrl = "https://dev.azure.com/ceapex";
        //==========================================================================

        //Console application to execute a user defined work item query
        static void Main(string[] args)
        {
            //Prompt user for credential
            VssConnection connection = new VssConnection(new Uri(azureDevOpsOrganizationUrl), new VssClientCredentials());

            //create http client and query for resutls
            WorkItemTrackingHttpClient witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            Wiql query = new Wiql() { Query = "SELECT [Id], [Title], [State] FROM workitems WHERE [Assigned To] = @Me" };
            WorkItemQueryResult queryResults = witClient.QueryByWiqlAsync(query).Result;
            GitHttpClient gitClient = connection.GetClient<GitHttpClient>();

            ProjectHttpClient projectClient = connection.GetClient<ProjectHttpClient>();
            TeamHttpClient teamClient = connection.GetClient<TeamHttpClient>();
            PolicyHttpClient policyClient = connection.GetClient<PolicyHttpClient>();

            // Call to get the list of projects
            IEnumerable<TeamProjectReference> projects = projectClient.GetProjects().Result;

            Dictionary<TeamProjectReference, IEnumerable<WebApiTeam>> results = new Dictionary<TeamProjectReference, IEnumerable<WebApiTeam>>();

            IList<RepoBranchStatus> result = new List<RepoBranchStatus>();

            int count = 0;
            List<GitBranchStats> branches = new List<GitBranchStats>();

            // Iterate over the returned projects
            foreach (var project in projects)
            {
                Console.WriteLine($"Project: {project.Name}");
                IDictionary<string, IList<PolicyConfiguration>> policyDict = new Dictionary<string, IList<PolicyConfiguration>>();
                foreach (var policy in policyClient.GetPolicyConfigurationsAsync(project.Id).Result)
                {
                    if (policy.IsEnabled && policy.Settings["scope"].HasValues)
                    {
                        var scope = policy.Settings["scope"].FirstOrDefault();
                        if (scope["repositoryId"] != null && scope["refName"] != null)
                        {
                            var repositoryId = scope["repositoryId"].ToString();
                            var branch = scope["refName"].ToString().Replace("refs/heads/", "");
                            var repoBranch = $"{repositoryId}-{branch}";
                            if (!policyDict.ContainsKey(repoBranch))
                            {
                                policyDict[repoBranch] = new List<PolicyConfiguration>();
                            }

                            policyDict[repoBranch].Add(policy);
                        }
                    }
                }

                foreach (var repo in gitClient.GetRepositoriesAsync(project.Id).Result)
                {
                    try
                    {
                        branches = gitClient.GetBranchesAsync(repo.Id).Result;
                    }
                    catch
                    {
                    }

                    foreach (var branch in branches.Where(branch => branch.IsBaseVersion))
                    {
                        Console.WriteLine($"({++count}) {repo.Name} - {branch.Name}");
                        var repoBranchStatus = new RepoBranchStatus()
                        {
                            Project = project.Name,
                            RepoName = repo.Name,
                            Branch = branch.Name
                        };

                        if (branch.Commit.Author.Date >= DateTime.Now.AddMonths(-3))
                        {
                            repoBranchStatus.Active = true;
                        }

                        try
                        {
                            var readme = gitClient.GetItemAsync(repo.Id, "README.md", includeContent: true).Result;
                            if (!readme.Content.Contains("TODO: Give a short introduction of your project."))
                            {
                                repoBranchStatus.HasReadMe = true;
                            }
                                
                            if (readme.Content.ToLower().Contains("owner") || readme.Content.ToLower().Contains("contact")
                                || EmailRegex.IsMatch(readme.Content))
                            {
                                repoBranchStatus.HasOwner = true;
                            }
                        }
                        catch
                        {
                        }

                        var repositoryId = repo.Id.ToString();
                        var repoBranch = $"{repositoryId}-{branch.Name}";
                        if (policyDict.ContainsKey(repoBranch))
                        {
                            repoBranchStatus.HasPolicy = true;
                            var policies = policyDict[repoBranch];
                            repoBranchStatus.Policies = policies.Select(p =>
                            {
                                if (p.Type.DisplayName == "Minimum number of reviewers")
                                {
                                    return $"{p.Type.DisplayName}: {p.Settings["minimumApproverCount"]} {(p.Settings["creatorVoteCounts"].ToString() == "True" ? "(creatorVoteCounts)" : "")}";
                                }
                                else if (p.Type.DisplayName == "Require a merge strategy")
                                {
                                    return $"{p.Type.DisplayName}: {((p.Settings["useSquashMerge"].ToString() == "True") ? "Squash" : "Non-Squash")}";
                                }
                                else if (p.Type.DisplayName == "Build")
                                {
                                    return "Require a successful build before updating protected refs";
                                }
                                else if (p.Type.DisplayName == "Status")
                                {
                                    return "Require a successful status to be posted before updating protected refs";
                                }
                                else if (p.Type.DisplayName == "Comment requirements")
                                {
                                    return "Check if the pull request has any active comments";
                                }

                                return p.Type.DisplayName;
                            }).ToList();
                        }

                        result.Add(repoBranchStatus);
                    }
                }
            }

            if (projects.Count() == 0)
            {
                Console.WriteLine("No projects found.");
            }
            else
            {
                OutputResult(result);
            }

            //Display reults in console
            /*
            if (queryResults == null || queryResults.WorkItems.Count() == 0)
            {
                Console.WriteLine("Query did not find any results");
            }
            else
            {
                int count = 0;
                foreach (var item in queryResults.WorkItems)
                {
                    count++;
                    var workItem = witClient.GetWorkItemAsync(item.Id).Result;
                    Console.WriteLine($"[{count}]{workItem.Fields["System.WorkItemType"]} {workItem.Id}: {workItem.Fields["System.Title"]}");
                }
            }
            */

            Console.ReadLine();
        }

        public static void OutputResult(IList<RepoBranchStatus> repoBranchStatuses)
        {
            Console.WriteLine("| Project | Repository Name | Branch | Is Active | Has README.md | Has Owner | Has Policy | Policies |");
            Console.WriteLine("| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |");
            foreach (var repoBranchStatus in repoBranchStatuses.OrderBy(rb => rb.RepoName).OrderBy(rb => rb.Project).OrderBy(rb => !rb.Active))
            {
                Console.WriteLine($"| {repoBranchStatus.Project} | {repoBranchStatus.RepoName} | {repoBranchStatus.Branch} | {repoBranchStatus.Active.ToString()} | {repoBranchStatus.HasReadMe.ToString()} | {repoBranchStatus.HasOwner.ToString()} | {repoBranchStatus.HasPolicy.ToString()} | {string.Join("<br><br>", repoBranchStatus.Policies)} |");
            }
        }
    }
}
