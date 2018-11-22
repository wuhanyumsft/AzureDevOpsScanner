using System.Collections.Generic;

namespace AzureDevOpsScanner
{
    public class RepoBranchStatus
    {
        public RepoBranchStatus()
        {
            Policies = new List<string>();
        }

        public string Project { get; set; }

        public string RepoName { get; set; }

        public string Branch { get; set; }

        public bool Active { get; set; }

        public bool IsDefaultBranch { get; set; }

        public bool HasReadMe { get; set; }

        public bool HasOwner { get; set; }

        public bool HasPolicy { get; set; }

        public IList<string> Policies { get; set; }
    }
}
