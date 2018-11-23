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
using System.IO;
using CsvHelper;

namespace AzureDevOpsScanner
{
    class Program
    {
        //============= Config [Edit these with your settings] =====================
        private const string azureDevOpsOrganizationUrl = "https://dev.azure.com/ceapex";
        //==========================================================================

        private static GitHttpClient gitClient;
        private static ProjectHttpClient projectClient;
        private static TeamHttpClient teamClient;
        private static PolicyHttpClient policyClient;
        private static WorkItemTrackingHttpClient witClient;
        private static IDictionary<int, bool> hasCodeChangeDict = new Dictionary<int, bool>();

        //Console application to execute a user defined work item query
        static void Main(string[] args)
        {
            //Prompt user for credential
            VssConnection connection = new VssConnection(new Uri(azureDevOpsOrganizationUrl), new VssClientCredentials());

            //create http client and query for resutls
            witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            gitClient = connection.GetClient<GitHttpClient>();
            projectClient = connection.GetClient<ProjectHttpClient>();
            teamClient = connection.GetClient<TeamHttpClient>();
            policyClient = connection.GetClient<PolicyHttpClient>();

            Wiql query = new Wiql() { Query = "SELECT [Id], [Title], [State] FROM workitems WHERE [Work Item Type] = 'Feature'" };
            WorkItemQueryResult queryResults = witClient.QueryByWiqlAsync(query).Result;

            if (queryResults == null || queryResults.WorkItems.Count() == 0)
            {
                Console.WriteLine("Query did not find any results");
            }
            else
            {
                var result = new List<FeatureStatus>();
                int count = 0;
                var featureIds = queryResults.WorkItems.Select(item => item.Id);
                for (var i = 0; i < featureIds.Count() / 100 + 1; i++)
                {
                    var workItems = witClient.GetWorkItemsAsync(featureIds.Skip(i * 100).Take(100), expand: WorkItemExpand.All).Result;
                    foreach (var workItem in workItems)
                    {
                        var hasCodeChange = CheckIfWorkItemConnectedWithCommitOrPullRequest(workItem);
                        result.Add(new FeatureStatus()
                        {
                            Id = workItem.Id.Value,
                            Title = workItem.Fields["System.Title"].ToString(),
                            Project = workItem.Fields["System.TeamProject"].ToString(),
                            Status = workItem.Fields["System.State"].ToString(),
                            IsConnectedWithCommitOrPullRequest = hasCodeChange
                        });
                        hasCodeChangeDict[workItem.Id.Value] = hasCodeChange;
                        Console.WriteLine($"[{++count}]{workItem.Fields["System.WorkItemType"]} {workItem.Id}: {workItem.Fields["System.Title"]}");
                    }
                }

                OutputResultCsv(result);
            }
            

            // GetRepositoryStatus();

            Console.WriteLine("Process finish.");
            Console.ReadLine();
        }

        public static bool CheckIfWorkItemConnectedWithCommitOrPullRequest(WorkItem workItem = null, bool allowExpand = true)
        {
            if (workItem.Relations == null)
            {
                return false;
            }

            foreach (var relation in workItem.Relations)
            {
                if (relation.Rel == "ArtifactLink")
                {
                    return true;
                }
            }

            if (!allowExpand)
            {
                return false;
            }

            var workItemType = workItem.Fields["System.WorkItemType"].ToString();

            var relationIdLookup = workItem.Relations.Where(relation => relation.Rel == "System.LinkTypes.Hierarchy-Forward" || relation.Rel == "System.LinkTypes.Related")
                                    .ToLookup(relation => int.Parse(relation.Url.Split('/').Last()), relation => relation.Rel);

            if (relationIdLookup.Where(relation => hasCodeChangeDict.ContainsKey(relation.Key) && hasCodeChangeDict[relation.Key]).Count() > 0)
            {
                return true;
            }

            var uncheckedRelationIds = relationIdLookup.Where(relation => !hasCodeChangeDict.ContainsKey(relation.Key)).Select(relation => relation.Key);
            if (uncheckedRelationIds.Count() > 0)
            {
                var newWorkItems = witClient.GetWorkItemsAsync(uncheckedRelationIds, expand: WorkItemExpand.Relations).Result;

                foreach (var newWorkItem in newWorkItems)
                {
                    if (newWorkItem.Id.HasValue)
                    {
                        var newWorkItemId = newWorkItem.Id.Value;
                        if (hasCodeChangeDict.ContainsKey(newWorkItemId))
                        {
                            return hasCodeChangeDict[newWorkItemId];
                        }

                        if (CheckIfWorkItemConnectedWithCommitOrPullRequest(newWorkItem, !relationIdLookup[newWorkItemId].Contains("System.LinkTypes.Related")))
                        {
                            hasCodeChangeDict[newWorkItemId] = true;
                            return true;
                        }
                    }
                }
            }

            if (allowExpand)
            {
                hasCodeChangeDict[workItem.Id.Value] = false;
            }

            return false;
        }

        public static void GetRepositoryStatus()
        {
            // Call to get the list of projects
            IEnumerable<TeamProjectReference> projects = projectClient.GetProjects().Result;

            IList<RepoBranchStatus> result = new List<RepoBranchStatus>();

            int count = 0;
            List<GitBranchStats> branches = new List<GitBranchStats>();

            // Iterate over the returned projects
            foreach (var project in projects.Where(project => project.Name == "Engineering"))
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
                            Branch = branch.Name,
                            IsDefaultBranch = repo.DefaultBranch.Split('/').Last() == branch.Name,
                            Active = branch.Commit.Author.Date >= DateTime.Now.AddMonths(-3)
                        };

                        try
                        {
                            var readme = gitClient.GetItemAsync(repo.Id, "README.md", includeContent: true).Result;
                            if (!readme.Content.Contains("TODO: Give a short introduction of your project."))
                            {
                                repoBranchStatus.HasReadMe = true;
                            }

                            if (readme.Content.ToLower().Contains("owner") || readme.Content.ToLower().Contains("contact")
                                || readme.Content.ToLower().Contains("@microsoft.com"))
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
                OutputResultCsv(result);
            }
        }

        public static void OutputResultCsv(IList<RepoBranchStatus> repoBranchStatuses)
        {
            using (StreamWriter sw = new StreamWriter("result_repobranch.csv"))
            {
                var writer = new CsvWriter(sw);
                writer.Configuration.Delimiter = ",";
                writer.WriteField("Project");
                writer.WriteField("Repository Name");
                writer.WriteField("Branch");
                writer.WriteField("Is Default Branch");
                writer.WriteField("Is Active");
                writer.WriteField("Has README.md");
                writer.WriteField("Has Owner");
                writer.WriteField("Has Policy");
                writer.WriteField("Policies");
                writer.NextRecord();
  
                foreach (var repoBranchStatus in repoBranchStatuses.OrderBy(rb => rb.RepoName).OrderBy(rb => !rb.Active))
                {
                    writer.WriteField(repoBranchStatus.Project);
                    writer.WriteField(repoBranchStatus.RepoName);
                    writer.WriteField(repoBranchStatus.Branch);
                    writer.WriteField(repoBranchStatus.IsDefaultBranch);
                    writer.WriteField(repoBranchStatus.Active);
                    writer.WriteField(repoBranchStatus.HasReadMe);
                    writer.WriteField(repoBranchStatus.HasOwner);
                    writer.WriteField(repoBranchStatus.HasPolicy);
                    writer.WriteField(string.Join("\n", repoBranchStatus.Policies.Distinct().OrderBy(p => p)));
                    writer.NextRecord();
                }
            }
        }

        public static void OutputResultCsv(IList<FeatureStatus> featureStatuses)
        {
            using (StreamWriter sw = new StreamWriter("result_feature.csv"))
            {
                var writer = new CsvWriter(sw);
                writer.Configuration.Delimiter = ",";
                writer.WriteField("Project");
                writer.WriteField("Id");
                writer.WriteField("Title");
                writer.WriteField("Status");
                writer.WriteField("Is Connected With Commit Or PullRequest");
                writer.NextRecord();

                foreach (var feature in featureStatuses.OrderBy(feature => feature.Id))
                {
                    writer.WriteField(feature.Project);
                    writer.WriteField(feature.Id);
                    writer.WriteField(feature.Title);
                    writer.WriteField(feature.Status);
                    writer.WriteField(feature.IsConnectedWithCommitOrPullRequest);
                    writer.NextRecord();
                }
            }
        }
    }
}
