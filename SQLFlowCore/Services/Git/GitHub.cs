using Octokit;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SQLFlowCore.Services.Git
{
    /// <summary>
    /// Provides methods to interact with GitHub.
    /// </summary>
    internal class GitHub
    {

        /// <summary>
        /// Pushes the database scripts to a GitHub repository.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to write logs.</param>
        /// <param name="AccessToken">The access token for GitHub.</param>
        /// <param name="ScriptToPath">The path to the script.</param>
        /// <param name="Username">The GitHub username.</param>
        /// <param name="RepoName">The name of the GitHub repository.</param>
        /// <param name="DBName">The name of the database.</param>
        internal static void pushToGitHub(StreamWriter logWriter, string AccessToken, string ScriptToPath, string Username, string RepoName, string DBName)
        {
            var watch = new Stopwatch();
            watch.Start();

            GitHubClient gitHub = new GitHubClient(new ProductHeaderValue("sqlflow-github-client"));

            var tokenAuth = new Credentials(AccessToken);
            gitHub.Credentials = tokenAuth;

            var repos = gitHub.Repository.GetAllForCurrent().Result;
            bool repositoryFound = false;

            new Repository();

            foreach (var rep in repos)
            {
                if (rep.Name == RepoName)
                {
                    repositoryFound = true;
                }
                //arg.Add(RepoName.Owner.Login, RepoName.Name);
            }

            //RespositoryGitHub.get
            if (repositoryFound == false)
            {
                try
                {
                    var repository = new NewRepository(RepoName)
                    {
                        AutoInit = true,
                        Description = "Test Repo DB",
                        LicenseTemplate = "mit",
                        Private = true
                    };
                    var context = gitHub.Repository.Create(repository);

                    //RespositoryGitHub.
                    Console.WriteLine($"The respository {repository} was created.");
                }
                catch (AggregateException e)
                {
                    Console.WriteLine($"E: For some reason, the repository can't be created. It may already exist. {e.Message}");
                }
            }
            updateGithub(logWriter, ScriptToPath, gitHub, Username, RepoName, DBName);

            watch.Stop();
            long logDurationPre = watch.ElapsedMilliseconds / 1000;
            logWriter.Write($"## {DBName} syncronized with GitHub ({logDurationPre.ToString()} sec)  {Environment.NewLine}");
            logWriter.Flush();
        }

        /// <summary>
        /// Updates the GitHub repository with the latest database scripts.
        /// </summary>
        /// <param name="logWriter">The StreamWriter to write logs.</param>
        /// <param name="ScriptToPath">The path to the script.</param>
        /// <param name="github">The GitHubClient instance.</param>
        /// <param name="Username">The GitHub username.</param>
        /// <param name="RepoName">The name of the GitHub repository.</param>
        /// <param name="DBName">The name of the database.</param>
        static void updateGithub(StreamWriter logWriter, string ScriptToPath, GitHubClient github, string Username, string RepoName, string DBName)
        {
            var headMasterRef = "heads/main";
            var masterReference = github.Git.Reference.Get(Username, RepoName, headMasterRef).Result;
            var latestCommit = github.Git.Commit.Get(Username, RepoName, masterReference.Object.Sha).Result;

            TreeResponse treeResponse = github.Git.Tree.GetRecursive(Username, RepoName, masterReference.Object.Sha).Result;
            var AllGitDBFiles = treeResponse.Tree.Where(x => x.Path.Contains(DBName) && x.Mode == "100644");

            string LocalDBPath = ScriptToPath + DBName; //+ (RepoName.Length > 0 ? "\\" + RepoName : "")

            DirectoryInfo dirInfo = new DirectoryInfo(LocalDBPath);
            var AllLocalDBFiles = dirInfo.GetFiles("*.*", SearchOption.AllDirectories);
            //var AllLocalDBFiles = Directory.EnumerateFiles(LocalDBPath,"*.*", SearchOption.AllDirectories);

            Hashtable AllLocalGitPaths = new Hashtable();
            foreach (FileInfo file in AllLocalDBFiles)
            {
                string gitPath = file.FullName.Replace(ScriptToPath, "").Replace(@"\", "/");
                AllLocalGitPaths.Add(gitPath, file);
            }

            //string gitPath = LocalDBPath.Replace(ScriptToPath, "").Replace(@"\", "/");
            //var commitDetails = github.Repository.Commit.SMOScriptingOptions(Username, RepoName, latestCommit.Sha).Result;
            //var files = commitDetails.Files;

            //var tipCommit = github.Repository.Commit.SMOScriptingOptions(Username, RepoName, "main").Result;
            //var allPaths = tipCommit.Files.Select(f => f.Filename);

            var nt = new NewTree { BaseTree = latestCommit.Tree.Sha };

            List<string> NewGitFiles = new List<string>();
            // Add items based on blobs
            foreach (FileInfo fileInfo in AllLocalDBFiles)
            {
                string sExtension = fileInfo.Extension.ToLower();
                string sFilename = fileInfo.FullName.Replace(ScriptToPath, "").Replace(@"\", "/");
                NewGitFiles.Add(sFilename);

                //Console.WriteLine("File(Local): " + sFilename);
                switch (sExtension)
                {
                    default:
                        // Create text blob
                        var textBlob = new NewBlob { Encoding = EncodingType.Utf8, Content = File.ReadAllText(fileInfo.FullName) };
                        var textBlobRef = github.Git.Blob.Create(Username, RepoName, textBlob).Result;

                        nt.Tree.Add(new NewTreeItem { Path = sFilename, Mode = "100644", Type = TreeType.Blob, Sha = textBlobRef.Sha });
                        break;

                    case ".dll":
                    case ".png":
                    case ".jpeg":
                    case ".jpg":
                        // For image, get image content and convert it to base64
                        var imgBase64 = Convert.ToBase64String(File.ReadAllBytes(fileInfo.FullName));
                        // Create image blob
                        var imgBlob = new NewBlob { Encoding = EncodingType.Base64, Content = imgBase64 };
                        var imgBlobRef = github.Git.Blob.Create(Username, RepoName, imgBlob).Result;

                        nt.Tree.Add(new NewTreeItem { Path = sFilename, Mode = "100644", Type = TreeType.Blob, Sha = imgBlobRef.Sha });

                        break;
                }
            }

            var rootTree = github.Git.Tree.Create(Username, RepoName, nt).Result;

            // Create Commit
            string CommitName = DBName + " " + DateTime.Now.ToLongDateString();
            var newCommit = new NewCommit(CommitName, rootTree.Sha, masterReference.Object.Sha);
            var commit = github.Git.Commit.Create(Username, RepoName, newCommit).Result;

            _ = github.Git.Reference.Update(Username, RepoName, headMasterRef, new ReferenceUpdate(commit.Sha)).Result;

            //Delete Files that are not in last comit
            foreach (var objRef in AllGitDBFiles)
            {
                if (NewGitFiles.Contains(objRef.Path) == false)
                {
                    DeleteFileRequest del = new DeleteFileRequest($"Deleting Removed Objects {DateTime.Now.ToLongDateString()}", objRef.Sha);
                    Task test = github.Repository.Content.DeleteFile(Username, RepoName, objRef.Path, del);
                    Task.WaitAll(test);
                }
            }
        }
    }
}
