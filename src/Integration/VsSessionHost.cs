//-----------------------------------------------------------------------
// <copyright file="VsSessionHost.cs" company="SonarSource SA and Microsoft Corporation">
//   Copyright (c) SonarSource SA and Microsoft Corporation.  All rights reserved.
//   Licensed under the MIT License. See License.txt in the project root for license information.
// </copyright>
//-----------------------------------------------------------------------

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using SonarLint.VisualStudio.Integration.Persistence;
using SonarLint.VisualStudio.Integration.Progress;
using SonarLint.VisualStudio.Integration.Service;
using SonarLint.VisualStudio.Integration.State;
using SonarLint.VisualStudio.Integration.TeamExplorer;
using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Windows.Threading;

namespace SonarLint.VisualStudio.Integration
{
    [Export(typeof(IHost))]
    [PartCreationPolicy(CreationPolicy.Shared)]
    internal sealed class VsSessionHost : IHost, IProgressStepRunnerWrapper, IDisposable
    {
        private readonly IServiceProvider serviceProvider;
        private readonly IActiveSolutionTracker solutionTacker;
        private readonly ISolutionBinding solutionBinding;
        private readonly IProgressStepRunnerWrapper progressStepRunner;

        private bool isDisposed;
        private bool resetBindingWhenAttaching = true;

        [ImportingConstructor]
        public VsSessionHost([Import(typeof(SVsServiceProvider))] IServiceProvider serviceProvider, SonarQubeServiceWrapper sonarQubeService, IActiveSolutionTracker solutionTacker)
            : this(serviceProvider, null, null, sonarQubeService, solutionTacker, new SolutionBinding(serviceProvider), Dispatcher.CurrentDispatcher)
        {
            Debug.Assert(ThreadHelper.CheckAccess(), "Expected to be created on the UI thread");
        }

        internal /*for test purposes*/ VsSessionHost(IServiceProvider serviceProvider,
                                    IStateManager state,
                                    IProgressStepRunnerWrapper progressStepRunner,
                                    ISonarQubeServiceWrapper sonarQubeService,
                                    IActiveSolutionTracker solutionTacker,
                                    ISolutionBinding solutionBinding,
                                    Dispatcher uiDispatcher)
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (sonarQubeService == null)
            {
                throw new ArgumentNullException(nameof(sonarQubeService));
            }

            if (solutionTacker == null)
            {
                throw new ArgumentNullException(nameof(solutionTacker));
            }

            if (uiDispatcher == null)
            {
                throw new ArgumentNullException(nameof(uiDispatcher));
            }

            if (solutionBinding == null)
            {
                throw new ArgumentNullException(nameof(solutionBinding));
            }

            this.serviceProvider = serviceProvider;
            this.VisualStateManager = state ?? new StateManager(this, new TransferableVisualState());
            this.progressStepRunner = progressStepRunner ?? this;
            this.UIDispatcher = uiDispatcher;
            this.SonarQubeService = sonarQubeService;
            this.solutionBinding = solutionBinding;
            this.solutionTacker = solutionTacker;
            this.solutionTacker.ActiveSolutionChanged += this.OnActiveSolutionChanged;
        }

        #region IProgressStepRunnerWrapper
        void IProgressStepRunnerWrapper.AbortAll()
        {
            ProgressStepRunner.AbortAll();
        }

        void IProgressStepRunnerWrapper.ChangeHost(IProgressControlHost host)
        {
            ProgressStepRunner.ChangeHost(host);
        }
        #endregion

        #region IHost
        public Dispatcher UIDispatcher { get; }

        public IStateManager VisualStateManager { get;  }

        public ISonarQubeServiceWrapper SonarQubeService { get; }

        public ISectionController ActiveSection { get; private set; }

        public void SetActiveSection(ISectionController section)
        {
            if (section == null)
            {
                throw new ArgumentNullException(nameof(section));
            }

            Debug.Assert(this.ActiveSection == null, "Already attached. Detach first");
            this.ActiveSection = section;
            this.VisualStateManager.SyncCommandFromActiveSection();

            this.TransferState();

            if (this.resetBindingWhenAttaching)
            {
                this.resetBindingWhenAttaching = false;

                // The connect section activated after the solution is opened, 
                // so reset the binding if applicable. No reason to abort since
                // this is the first time after the solution was opened so that 
                // we switched to the connect section.
                this.ResetBinding(abortCurrentlyRunningWorklows: false);
            }
        }

        public void ClearActiveSection()
        {
            if (this.ActiveSection == null) // Can be called multiple times
            {
                return;
            }

            this.ActiveSection.ViewModel.State = null;
            this.ActiveSection = null;
            this.VisualStateManager.SyncCommandFromActiveSection();
        }

        private void TransferState()
        {
            Debug.Assert(this.ActiveSection != null, "Not attached to any section attached");

            if (this.ActiveSection != null)
            {
                this.ActiveSection.ViewModel.State = this.VisualStateManager.ManagedState;

                IProgressControlHost progressHost = this.ActiveSection.ProgressHost;
                Debug.Assert(progressHost != null, "IProgressControlHost is expected");
                this.progressStepRunner.ChangeHost(progressHost);
            }
        }

        #endregion

        #region Active solution changed event handler
        private void OnActiveSolutionChanged(object sender, EventArgs e)
        {
            // Reset, and abort workflows
            this.ResetBinding(abortCurrentlyRunningWorklows: true);
        }

        private void ResetBinding(bool abortCurrentlyRunningWorklows)
        {
            if (abortCurrentlyRunningWorklows)
            {
                // We may have running workflows, abort them before proceeding
                this.progressStepRunner.AbortAll();
            }

            // Get the binding info (null if there's none i.e. when solution is closed or not bound)
            BoundSonarQubeProject bound = this.SafeReadBindingInformation();
            if (bound == null)
            {
                this.ClearCurrentBinding();
            }
            else
            {
                if (this.ActiveSection == null)
                {
                    // In case the connect section is not active, make it so that next time it activates
                    // it will reset the binding then.
                    this.resetBindingWhenAttaching = true;
                }
                else
                {
                    this.ApplyBindingInformation(bound);
                }
            }
        }

        private void ClearCurrentBinding()
        {
            this.VisualStateManager.BoundProjectKey = null;

            this.VisualStateManager.ClearBoundProject();
        }

        private void ApplyBindingInformation(BoundSonarQubeProject bound)
        {
            Debug.Assert(bound != null);
            Debug.Assert(bound?.ServerUri != null, "Will not be able to apply binding without server Uri");

            // Set the project key that should become bound once the connection workflow has completed
            this.VisualStateManager.BoundProjectKey = bound.ProjectKey;

            // Recreate the connection information from what was persisted
            ConnectionInformation connectionInformation = bound.Credentials == null ?
                new ConnectionInformation(bound.ServerUri)
                : bound.Credentials.CreateConnectionInformation(bound.ServerUri);

            Debug.Assert(this.ActiveSection != null, "Expected ActiveSection to be set");
            Debug.Assert(this.ActiveSection?.RefreshCommand != null, "Refresh command is not set");
            // Run the refresh workflow, passing the connection information
            var refreshCmd = this.ActiveSection.RefreshCommand;
            if (refreshCmd.CanExecute(connectionInformation))
            {
                refreshCmd.Execute(connectionInformation); // start the workflow
            }
        }

        private BoundSonarQubeProject SafeReadBindingInformation()
        {
            BoundSonarQubeProject bound = null;
            try
            {
                bound = this.solutionBinding.ReadSolutionBinding();
            }
            catch (Exception ex)
            {
                if (ErrorHandler.IsCriticalException(ex))
                {
                    throw;
                }

                Debug.Fail("Unexpected exception: " + ex.ToString());
            }

            return bound;
        }
        #endregion

        #region IServiceProvider
        public object GetService(Type type)
        {
            return this.serviceProvider.GetService(type);
        }
        #endregion

        #region IDisposable Support
        private void Dispose(bool disposing)
        {
            if (!this.isDisposed)
            {
                if (disposing)
                {
                    this.solutionTacker.ActiveSolutionChanged -= this.OnActiveSolutionChanged;
                }

                this.isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
        #endregion
    }
}