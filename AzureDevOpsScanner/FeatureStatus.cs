namespace AzureDevOpsScanner
{
    public class FeatureStatus
    {
        public int Id { get; set; }

        public string Project { get; set; }

        public string Title { get; set; }

        public string Status { get; set; }

        public bool IsConnectedWithCommitOrPullRequest { get; set; }
    }
}
