using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using EnvDTE;
using Microsoft.VisualStudio.ExtensionsExplorer;
using NuGet.Resolver;
using NuGet.VisualStudio;

namespace NuGet.Dialog.Providers
{
    /// <summary>
    /// IVsExtensionsProvider implementation responsible for gathering
    /// a list of packages from a package feed which will be shown in the Add NuGet dialog.
    /// </summary>
    internal class OnlineProvider : PackagesProviderBase
    {
        private readonly IPackageRepositoryFactory _packageRepositoryFactory;
        private readonly IPackageSourceProvider _packageSourceProvider;
        private readonly IVsPackageManagerFactory _packageManagerFactory;
        private readonly Project _project;

        public OnlineProvider(
            Project project,
            IPackageRepository localRepository,
            ResourceDictionary resources,
            IPackageRepositoryFactory packageRepositoryFactory,
            IPackageSourceProvider packageSourceProvider,
            IVsPackageManagerFactory packageManagerFactory,
            ProviderServices providerServices,
            IProgressProvider progressProvider,
            ISolutionManager solutionManager) :
            base(localRepository, resources, providerServices, progressProvider, solutionManager)
        {
            _packageRepositoryFactory = packageRepositoryFactory;
            _packageSourceProvider = packageSourceProvider;
            _packageManagerFactory = packageManagerFactory;
            _project = project;
        }

        public override string Name
        {
            get
            {
                return Resources.Dialog_OnlineProvider;
            }
        }

        public override float SortOrder
        {
            get
            {
                return 2.0f;
            }
        }

        public override bool RefreshOnNodeSelection
        {
            get
            {
                // only refresh if the current node doesn't have any extensions
                return (SelectedNode == null || SelectedNode.Extensions.Count == 0);
            }
        }

        public override IEnumerable<string> SupportedFrameworks
        {
            get
            {
                string targetFramework = _project.GetTargetFramework();
                return targetFramework != null ? new[] { targetFramework } : new string[0];
            }
        }

        public virtual string OperationName
        {
            get { return RepositoryOperationNames.Install; }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Design",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "We want to suppress all errors to show an empty node.")]
        protected override void FillRootNodes()
        {
            var packageSources = _packageSourceProvider.GetEnabledPackageSourcesWithAggregate();
           
            // create one tree node per package source
            foreach (var source in packageSources)
            {
                PackagesTreeNodeBase node;
                try
                {
                    var repository = new LazyRepository(_packageRepositoryFactory, source);
                    node = CreateTreeNodeForPackageSource(source, repository);
                }
                catch (Exception exception)
                {
                    // exception occurs if the Source value is invalid. In which case, adds an empty tree node in place.
                    node = new EmptyTreeNode(this, source.Name, RootNode);
                    ExceptionHelper.WriteToActivityLog(exception);
                }

                RootNode.Nodes.Add(node);
            }

            if (RootNode.Nodes.Count >= 2)
            {
                // Bug #628 : Do not set aggregate source as default because it 
                // will slow down the dialog when querying two or more sources.
                RootNode.Nodes[1].IsSelected = true;
            }
        }

        protected virtual PackagesTreeNodeBase CreateTreeNodeForPackageSource(PackageSource source, IPackageRepository repository)
        {
            return new OnlineTreeNode(this, source.Name, RootNode, repository);
        }

        protected internal virtual IVsPackageManager GetActivePackageManager()
        {
            if (SelectedNode == null)
            {
                return null;
            }
            else if (SelectedNode.IsSearchResultsNode)
            {
                PackagesSearchNode searchNode = (PackagesSearchNode)SelectedNode;
                SimpleTreeNode baseNode = (SimpleTreeNode)searchNode.BaseNode;
                return _packageManagerFactory.CreatePackageManager(baseNode.Repository, useFallbackForDependencies: true);
            }
            else
            {
                var selectedNode = SelectedNode as SimpleTreeNode;
                return (selectedNode != null) ? _packageManagerFactory.CreatePackageManager(selectedNode.Repository, useFallbackForDependencies: true) : null;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Design",
            "CA1031:DoNotCatchGeneralExceptionTypes",
            Justification = "We don't want one failed project to affect the other projects.")]
        protected override bool ExecuteCore(PackageItem item)
        {
            IVsPackageManager activePackageManager = GetActivePackageManager();
            Debug.Assert(activePackageManager != null);

            using (activePackageManager.SourceRepository.StartOperation(OperationName, item.Id, item.Version))
            {
                ShowProgressWindow();

                // Resolve operations
                var resolver = new ActionResolver()
                {
                    Logger = this,
                    DependencyVersion = activePackageManager.DependencyVersion,
                    IgnoreDependencies = false,
                    AllowPrereleaseVersions = IncludePrerelease
                };
                var projectManager = activePackageManager.GetProjectManager(_project);
                projectManager.Logger = this;
                resolver.AddOperation(PackageAction.Install, item.PackageIdentity, projectManager);
                var actions = resolver.ResolveActions();

                // show license agreeement
                bool acceptLicense = ShowLicenseAgreement(actions);
                if (!acceptLicense)
                {
                    return false;
                }

                // execute operations
                try
                {
                    RegisterPackageOperationEvents(activePackageManager, projectManager);

                    var actionExecutor = new ActionExecutor()
                    {
                        Logger = this
                    };
                    actionExecutor.Execute(actions);
                }
                finally
                {
                    UnregisterPackageOperationEvents(activePackageManager, projectManager);
                }

                return true;
            }
        }

        public override bool CanExecute(PackageItem item)
        {
            var latestPackageLookup = LocalRepository as ILatestPackageLookup;
            if (latestPackageLookup != null)
            {
                // in this case, we mark this package as installed if the current project has 
                // any lower-or-equal-versioned package with the same id installed.
                SemanticVersion installedVersion;
                return !latestPackageLookup.TryFindLatestPackageById(item.Id, out installedVersion) ||
                       installedVersion < item.PackageIdentity.Version;
            }
            else
            {
                // Only enable command on a Package in the Online provider if it is not installed yet
                return !LocalRepository.Exists(item.PackageIdentity);
            }
        }

        public override IVsExtension CreateExtension(IPackage package)
        {
            return new PackageItem(this, package)
            {
                CommandName = Resources.Dialog_InstallButton,
                TargetFramework = _project.GetTargetFrameworkName()
            };
        }

        protected override PackagesProviderBase GetSearchProvider()
        {
            var baseProvider = base.GetSearchProvider();
            return new OnlineSearchProvider(baseProvider);
        }

        public override string NoItemsMessage
        {
            get
            {
                return Resources.Dialog_OnlineProviderNoItem;
            }
        }

        public override string ProgressWindowTitle
        {
            get
            {
                return Dialog.Resources.Dialog_InstallProgress;
            }
        }

        protected override string GetProgressMessage(IPackage package)
        {
            return Resources.Dialog_InstallProgress + package.ToString();
        }
    }
}
