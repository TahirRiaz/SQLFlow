using LibGit2Sharp;
using SharpBucket.V2;
using SharpBucket.V2.EndPoints;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
namespace SQLFlowCore.Services.Git
{
    /// <summary>
    /// Represents a helper class for Bitbucket operations.
    /// </summary>
    internal class BitBucket
    {
        /// <summary>
        /// Builds the repository URL for a given username and repository name.
        /// </summary>
        /// <param name="username">The username of the Bitbucket account.</param>
        /// <param name="RepoName">The name of the repository.</param>
        /// <returns>A string representing the URL of the Bitbucket repository.</returns>
        internal static string BuilRepURL(string username, string RepoName)
        {
            string postedRepoUrl = $@"https://{username}@bitbucket.org/{username}/{RepoName}.git";

            return postedRepoUrl;
        }

        /// <summary>
        /// Cleans up the directory specified by the path.
        /// </summary>
        /// <param name="ScriptToPath">The path to the directory to clean up.</param>
        /// <remarks>
        /// This method deletes all files and subdirectories in the specified directory.
        /// </remarks>
        internal static void DirCleanUp(string ScriptToPath)
        {
            DirectoryInfo di = new DirectoryInfo(ScriptToPath);
            foreach (FileInfo file in di.GetFiles("*.*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file.FullName, FileAttributes.Normal);
                file.Delete();
            }
            foreach (DirectoryInfo d in di.GetDirectories()) //"*", SearchOption.AllDirectories
            {
                d.Delete(true);
            }
        }

        /// <summary>
        /// Pushes the changes to the Git repository.
        /// </summary>
        /// <param name="logWriter">The StreamWriter object to write logs.</param>
        /// <param name="ScriptToPath">The path to the script.</param>
        /// <param name="username">The username for the Git repository.</param>
        /// <param name="RepoName">The name of the Git repository.</param>
        /// <param name="DBName">The name of the database.</param>
        /// <param name="WorkSpaceName">The name of the workspace.</param>
        /// <param name="ProjectName">The name of the project.</param>
        /// <param name="ProjectKey">The key of the project.</param>
        /// <param name="ConsumerKey">The consumer key for the OAuth2 authentication.</param>
        /// <param name="ConsumerSecret">The consumer secret for the OAuth2 authentication.</param>
        internal static void PushToGit(StreamWriter logWriter, string ScriptToPath, string username, string RepoName, string DBName, string WorkSpaceName, string ProjectName, string ProjectKey, string ConsumerKey, string ConsumerSecret)
        {
            var watch = new Stopwatch();
            watch.Start();
            //currentRepository.mainbranch.
            IGitCredentialsProvider credentialProvider = new OAuth2GitCredentialsProvider(ConsumerKey, ConsumerSecret);
            var cloneOptions = new CloneOptions()

            {
                FetchOptions = { CredentialsProvider = credentialProvider.GetCredentials }
            };

            var signature = new Signature(username, "fake@test.com", DateTime.Now);
            var pushOptions = new PushOptions { CredentialsProvider = credentialProvider.GetCredentials };

            string repositoryUrl = BuilRepURL(username, RepoName);
            Repository.ListRemoteReferences(repositoryUrl, credentialProvider.GetCredentials);

            using (var LibRepo = new Repository(ScriptToPath))
            {
                string dbFolder = ScriptToPath + DBName;
                DirectoryInfo dirInfo = new DirectoryInfo(dbFolder);
                var AllLocalDBFiles = dirInfo.GetFiles("*.*", SearchOption.AllDirectories);

                foreach (var AllLocalDBFile in AllLocalDBFiles)
                {
                    Commands.Stage(LibRepo, AllLocalDBFile.FullName);
                }

                //List Deleted Files
                RepositoryStatus status = LibRepo.RetrieveStatus(new StatusOptions() { IncludeUnaltered = true | false });

                foreach (var s in status)
                {
                    if (s.State == FileStatus.DeletedFromWorkdir && s.FilePath.Contains(DBName, StringComparison.InvariantCultureIgnoreCase) == true)
                    {
                        Commands.Remove(LibRepo, s.FilePath);
                    }
                }

                //CheckForError If there are files to push
                RepositoryStatus currentStatus = LibRepo.RetrieveStatus(new StatusOptions());
                int Changes = currentStatus.Count();
                if (Changes > 1)
                {
                    LibRepo.Commit("SQLFlow Sync", signature, signature);
                    LibRepo.Network.Push(LibRepo.Head, pushOptions);

                    watch.Stop();
                    long logDurationPre = watch.ElapsedMilliseconds / 1000;
                    logWriter.Write($"## {DBName} syncronized with BitBucket ({logDurationPre.ToString()} sec)  {Environment.NewLine}");
                    logWriter.Flush();
                }
                else
                {
                    watch.Stop();
                    long logDurationPre = watch.ElapsedMilliseconds / 1000;
                    logWriter.Write($"## {DBName} nothing to commit ({logDurationPre.ToString()} sec)  {Environment.NewLine}");
                    logWriter.Flush();
                }
            }
        }

        /// <summary>
        /// Initializes a local Git repository.
        /// </summary>
        /// <param name="CreateWrkProjRepo">A boolean value indicating whether to create a workspace project repository.</param>
        /// <param name="ScriptToPath">The path to the script.</param>
        /// <param name="username">The username of the Bitbucket user.</param>
        /// <param name="RepoName">The name of the repository.</param>
        /// <param name="DBName">The name of the database.</param>
        /// <param name="WorkSpaceName">The name of the workspace.</param>
        /// <param name="ProjectName">The name of the project.</param>
        /// <param name="ProjectKey">The key of the project.</param>
        /// <param name="ConsumerKey">The consumer key for OAuth2 authentication.</param>
        /// <param name="ConsumerSecret">The consumer secret for OAuth2 authentication.</param>
        internal static void initLocalGit(bool CreateWrkProjRepo, string ScriptToPath, string username, string RepoName, string DBName, string WorkSpaceName, string ProjectName, string ProjectKey, string ConsumerKey, string ConsumerSecret)
        {
            new Stopwatch();

            //bool CreateWrkProjRepo = false;
            var sharpBucket = new SharpBucketV2();
            // authenticate with OAuth2 keys
            sharpBucket.OAuth2ClientCredentials(ConsumerKey, ConsumerSecret);

            UserEndpoint userEndPoint = sharpBucket.UserEndPoint();
            var user = userEndPoint.GetUser();

            if (CreateWrkProjRepo)
            {
                //Repository r = cRepo;
                //CheckForError the workspace and project
                WorkspacesEndPoint wep = sharpBucket.WorkspacesEndPoint();
                //SharpBucket.V2.Pocos.Workspace wX = wep.WorkspaceResource();

                List<SharpBucket.V2.Pocos.Workspace> workList = wep.ListWorkspaces();
                SharpBucket.V2.Pocos.Workspace curWorkspace = new SharpBucket.V2.Pocos.Workspace();
                foreach (SharpBucket.V2.Pocos.Workspace w in workList)
                {
                    if (w.name == WorkSpaceName)
                    {
                        curWorkspace = w;
                    }
                }

                //curWorkspace
                WorkspaceResource wr = wep.WorkspaceResource(curWorkspace.uuid.ToString());
                ProjectsResource pr = wr.ProjectsResource;

                List<SharpBucket.V2.Pocos.Project> projectList = pr.ListProjects();
                SharpBucket.V2.Pocos.Project currentProject = new SharpBucket.V2.Pocos.Project();
                bool projectFound = false;
                foreach (SharpBucket.V2.Pocos.Project p in projectList)
                {
                    if (p.name == ProjectName && p.key == ProjectKey)
                    {
                        currentProject = p;
                        projectFound = true;
                    }
                }

                if (projectFound == false)
                {
                    var project = new SharpBucket.V2.Pocos.Project
                    {
                        key = ProjectKey,
                        name = ProjectName,
                        is_private = true,
                        description = $"SQLFlow Project for source control",
                    };
                    currentProject = pr.PostProject(project);
                }





                RepositoriesEndPoint repositoriesEndPoint = sharpBucket.RepositoriesEndPoint();
                RepositoriesAccountResource repositoriesAccountResource = repositoriesEndPoint.RepositoriesResource(user.username); //user.username


                List<SharpBucket.V2.Pocos.Repository> repoList = repositoriesAccountResource.ListRepositories();
                bool repositoryFound = false;
                new SharpBucket.V2.Pocos.Repository();

                foreach (var rep in repoList)
                {
                    if (rep.name == RepoName)
                    {
                        repositoryFound = true;
                    }
                }

                //currentRepository.mainbranch.
                IGitCredentialsProvider credentialProvider = new OAuth2GitCredentialsProvider(ConsumerKey, ConsumerSecret);
                var cloneOptions = new CloneOptions
                {
                    FetchOptions = { CredentialsProvider = credentialProvider.GetCredentials }
                };

                var fetchOptions = new FetchOptions
                {
                    CredentialsProvider = credentialProvider.GetCredentials
                };

                var signature = new Signature(user.username, "fake@test.com", DateTime.Now);
                var pushOptions = new PushOptions { CredentialsProvider = credentialProvider.GetCredentials };

                if (repositoryFound == false)
                {
                    try
                    {
                        SharpBucket.V2.Pocos.ProjectInfo p = new SharpBucket.V2.Pocos.ProjectInfo
                        {
                            name = currentProject.name,
                            key = currentProject.key,
                            uuid = currentProject.uuid,
                            type = currentProject.type
                        };

                        SharpBucket.V2.Pocos.NamedBranch b = new SharpBucket.V2.Pocos.NamedBranch
                        {
                            name = "main"
                        };

                        var repository = new SharpBucket.V2.Pocos.Repository
                        {
                            description = "Repo DB",
                            is_private = true,
                            name = RepoName,
                            scm = "git",
                            mainbranch = b,
                            project = p
                        };
                        //repository.language = "sql";

                        //Create new repo
                        RepositoryResource repositoryResource = repositoriesEndPoint.RepositoryResource(user.username, RepoName);
                        repositoryResource.PostRepository(repository);

                        //Clone new repo to init local folder
                        string postedRepoUrl = $@"https://{user.username}@bitbucket.org/{user.username}/{RepoName}.git";
                        Repository.Clone(postedRepoUrl, ScriptToPath, cloneOptions);

                        string readMeFile = $"{ScriptToPath}Readme.md";
                        string fileContent = "SQLFlow Source Control";
                        var dir = Path.GetDirectoryName(readMeFile);
                        if (!string.IsNullOrEmpty(dir))
                        {
                            Directory.CreateDirectory($"{ScriptToPath}");
                        }
                        File.WriteAllText(readMeFile, fileContent);

                        var LibRepo = new Repository(ScriptToPath);
                        //LibGit2Sharp.Repository.Init()
                        Commands.Stage(LibRepo, readMeFile);
                        LibRepo.Commit("Init commit", signature, signature);
                        LibRepo.Network.Push(LibRepo.Head, pushOptions);
                        Console.WriteLine($"The respository was created.");
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine($"For some reason, the repository can't be created. It may already exist. {e.Message}");
                    }
                }
                else
                {
                    //Clone new repo to init local folder
                    string postedRepoUrl = $@"https://{user.username}@bitbucket.org/{user.username}/{RepoName}.git";
                    Repository.Clone(postedRepoUrl, ScriptToPath, cloneOptions);
                }
            }
            else
            {
                // your main entry to the Bitbucket API, this one is for V2
                RepositoriesEndPoint repoEndPoint = sharpBucket.RepositoriesEndPoint();
                RepositoryResource cRepo = repoEndPoint.RepositoryResource(username, RepoName);
                cRepo.GetRepository();
                string postedRepoUrl = $@"https://{user.username}@bitbucket.org/{user.username}/{RepoName}.git";

                IGitCredentialsProvider credentialProvider = new OAuth2GitCredentialsProvider(ConsumerKey, ConsumerSecret);
                var cloneOptions = new CloneOptions
                {
                    FetchOptions = { CredentialsProvider = credentialProvider.GetCredentials }
                };

                Repository.Clone(postedRepoUrl, ScriptToPath, cloneOptions);
            }




            //FetchOptions fop = new FetchOptions();
        }
    }
}
