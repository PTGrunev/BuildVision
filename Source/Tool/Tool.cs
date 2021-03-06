using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

using AlekseyNagovitsyn.BuildVision.Core.Common;
using AlekseyNagovitsyn.BuildVision.Core.Logging;
using AlekseyNagovitsyn.BuildVision.Helpers;
using AlekseyNagovitsyn.BuildVision.Tool.Building;
using AlekseyNagovitsyn.BuildVision.Tool.Models;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Indicators.Core;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Settings.BuildProgress;
using AlekseyNagovitsyn.BuildVision.Tool.Models.Settings.ToolWindow;
using AlekseyNagovitsyn.BuildVision.Tool.ViewModels;

using EnvDTE;
using Microsoft.VisualStudio.Shell.Interop;

using ProjectItem = AlekseyNagovitsyn.BuildVision.Tool.Models.ProjectItem;
using WindowState = AlekseyNagovitsyn.BuildVision.Tool.Models.Settings.ToolWindow.WindowState;

namespace AlekseyNagovitsyn.BuildVision.Tool
{
    public class Tool
    {
        private readonly DTE _dte;
        private readonly IVsStatusbar _dteStatusBar;
        private readonly ToolWindowManager _toolWindowManager;
        private readonly BuildInfo _buildContext;
        private readonly IBuildDistributor _buildDistributor;
        private readonly ControlViewModel _viewModel;
        private readonly SolutionEvents _solutionEvents;

        private bool _buildErrorIsNavigated;
        private string _origTextCurrentState;

        public Tool(
            IPackageContext packageContext,
            BuildInfo buildContext, 
            IBuildDistributor buildDistributor, 
            ControlViewModel viewModel)
        {
            _dte = packageContext.GetDTE();
            if (_dte == null)
                throw new InvalidOperationException("Unable to get DTE instance.");

            _dteStatusBar = packageContext.GetStatusBar();
            if (_dteStatusBar == null)
                TraceManager.TraceError("Unable to get IVsStatusbar instance.");

            _toolWindowManager = new ToolWindowManager(packageContext);

            _buildContext = buildContext;
            _buildDistributor = buildDistributor;

            _viewModel = viewModel;
            _solutionEvents = _dte.Events.SolutionEvents;

            Initialize();
        }

        private void Initialize()
        {
            _buildDistributor.OnBuildBegin += (s, e) => BuildEvents_OnBuildBegin();
            _buildDistributor.OnBuildDone += (s, e) => BuildEvents_OnBuildDone();
            _buildDistributor.OnBuildProcess += (s, e) => BuildEvents_OnBuildProcess();
            _buildDistributor.OnBuildCancelled += (s, e) => BuildEvents_OnBuildCancelled();
            _buildDistributor.OnBuildProjectBegin += BuildEvents_OnBuildProjectBegin;
            _buildDistributor.OnBuildProjectDone += BuildEvents_OnBuildProjectDone;
            _buildDistributor.OnErrorRaised += BuildEvents_OnErrorRaised;

            _solutionEvents.AfterClosing += () =>
                                            {
                                                _viewModel.TextCurrentState = Resources.BuildDoneText_BuildNotStarted;

                                                ControlTemplate stateImage;
                                                _viewModel.ImageCurrentState = BuildImages.GetBuildDoneImage(null, null, out stateImage);
                                                _viewModel.ImageCurrentStateResult = stateImage;

                                                UpdateSolutionItem();
                                                _viewModel.ProjectsList.Clear();

                                                _viewModel.ResetIndicators(ResetIndicatorMode.Disable);

                                                _viewModel.BuildProgressViewModel.ResetTaskBarInfo();
                                            };

            _solutionEvents.Opened += () =>
                                        {
                                            UpdateSolutionItem();
                                            _viewModel.ResetIndicators(ResetIndicatorMode.ResetValue);
                                        };

            UpdateSolutionItem();
        }

        private void UpdateSolutionItem()
        {
            _viewModel.SolutionItem.UpdateSolution(_dte.Solution);
        }

        private void BuildEvents_OnBuildProjectBegin(object sender, BuildProjectEventArgs e)
        {
            try
            {
                ProjectItem currentProject = e.ProjectItem;
                currentProject.State = e.ProjectState;
                currentProject.BuildFinishTime = null;
                currentProject.BuildStartTime = e.EventTime;

                _viewModel.OnBuildProjectBegin();
                if (_buildContext.BuildScope == vsBuildScope.vsBuildScopeSolution &&
                    (_buildContext.BuildAction == vsBuildAction.vsBuildActionBuild ||
                     _buildContext.BuildAction == vsBuildAction.vsBuildActionRebuildAll))
                {
                    currentProject.BuildOrder = _viewModel.BuildProgressViewModel.CurrentQueuePosOfBuildingProject;
                }

                if (!_viewModel.ProjectsList.Contains(currentProject))
                    _viewModel.ProjectsList.Add(currentProject);

                _viewModel.CurrentProject = currentProject;
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }
        }

        private void BuildEvents_OnBuildProjectDone(object sender, BuildProjectEventArgs e)
        {
            if (e.ProjectState == ProjectState.BuildError && _viewModel.ControlSettings.GeneralSettings.StopBuildAfterFirstError)
                _buildDistributor.CancelBuild();

            try
            {
                ProjectItem currentProject = e.ProjectItem;
                currentProject.State = e.ProjectState;
                currentProject.BuildFinishTime = DateTime.Now;
                currentProject.UpdatePostBuildProperties(e.BuildedProjectInfo);

                if (!_viewModel.ProjectsList.Contains(currentProject))
                    _viewModel.ProjectsList.Add(currentProject);

                var buildInfo = (BuildInfo)sender;
                if (ReferenceEquals(_viewModel.CurrentProject, e.ProjectItem) && buildInfo.BuildingProjects.Any())
                    _viewModel.CurrentProject = buildInfo.BuildingProjects.Last();
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }

            _viewModel.UpdateIndicators(_dte, _buildContext);

            try
            {
                _viewModel.OnBuildProjectDone(e.BuildedProjectInfo);
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }
        }

        private void BuildEvents_OnBuildBegin()
        {
            try
            {
                _buildErrorIsNavigated = false;

                ApplyToolWindowStateAction(_viewModel.ControlSettings.WindowSettings.WindowActionOnBuildBegin);

                UpdateSolutionItem();

                string message = BuildMessages.GetBuildBeginMajorMessage(
                    _viewModel.SolutionItem, 
                    _buildContext, 
                    _viewModel.ControlSettings.BuildMessagesSettings);

                OutputInStatusBar(message, true);
                _viewModel.TextCurrentState = message;
                _origTextCurrentState = message;
                _viewModel.ImageCurrentState = BuildImages.GetBuildBeginImage(_buildContext);
                _viewModel.ImageCurrentStateResult = null;

                if (_viewModel.ControlSettings.GeneralSettings.FillProjectListOnBuildBegin)
                {
                    _viewModel.SolutionItem.UpdateProjects();
                }
                else
                {
                    _viewModel.ProjectsList.Clear();                    
                }

                _viewModel.ResetIndicators(ResetIndicatorMode.ResetValue);

                _viewModel.OnBuildBegin(_buildContext);
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }
        }

        private void OutputInStatusBar(string str, bool freeze)
        {
            if (!_viewModel.ControlSettings.GeneralSettings.EnableStatusBarOutput) 
                return;

            if (_dteStatusBar == null)
                return;

            _dteStatusBar.FreezeOutput(0);
            _dteStatusBar.SetText(str);
            if (freeze)
                _dteStatusBar.FreezeOutput(1);
        }

        private void BuildEvents_OnBuildProcess()
        {
            try
            {
                var labelsSettings = _viewModel.ControlSettings.BuildMessagesSettings;
                string msg = _origTextCurrentState + BuildMessages.GetBuildBeginExtraMessage(_buildContext, labelsSettings);

                _viewModel.TextCurrentState = msg;
                OutputInStatusBar(msg, true);
                //_dte.SuppressUI = false;

                IReadOnlyList<ProjectItem> buildingProjects = _buildContext.BuildingProjects;
                lock (((ICollection)buildingProjects).SyncRoot)
                {
                    for (int i = 0; i < buildingProjects.Count; i++)
                        buildingProjects[i].RaiseBuildElapsedTimeChanged();
                }
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }
        }

        private void BuildEvents_OnBuildDone()
        {
            try
            {
                if (_buildContext.BuildScope == vsBuildScope.vsBuildScopeSolution)
                {
                    foreach (var projectItem in _viewModel.ProjectsList)
                    {
                        if (projectItem.State == ProjectState.Pending)
                            projectItem.State = ProjectState.Skipped;
                    }
                }

                _viewModel.UpdateIndicators(_dte, _buildContext);

                string message = BuildMessages.GetBuildDoneMessage(
                    _viewModel.SolutionItem, 
                    _buildContext, 
                    _viewModel.ControlSettings.BuildMessagesSettings);

                ControlTemplate stateImage;
                ControlTemplate buildDoneImage = BuildImages.GetBuildDoneImage(
                    _buildContext, 
                    _viewModel.ProjectsList, 
                    out stateImage);

                OutputInStatusBar(message, false);
                _viewModel.TextCurrentState = message;
                _viewModel.ImageCurrentState = buildDoneImage;
                _viewModel.ImageCurrentStateResult = stateImage;
                _viewModel.CurrentProject = null;

                _viewModel.OnBuildDone(_buildContext);

                int errorProjectsCount = _viewModel.ProjectsList.Count(item => item.State.IsErrorState());
                if (errorProjectsCount > 0 || _buildContext.BuildIsCancelled)
                    ApplyToolWindowStateAction(_viewModel.ControlSettings.WindowSettings.WindowActionOnBuildError);
                else
                    ApplyToolWindowStateAction(_viewModel.ControlSettings.WindowSettings.WindowActionOnBuildSuccess);

                bool navigateToBuildFailureReason = (!_buildContext.BuildedProjects.BuildWithoutErrors
                                                     && _viewModel.ControlSettings.GeneralSettings.NavigateToBuildFailureReason == NavigateToBuildFailureReasonCondition.OnBuildDone);
                if (navigateToBuildFailureReason)
                {
                    if (_buildContext.BuildedProjects.Any(p => p.ErrorsBox.Errors.Any(NavigateToErrorItem)))
                        _buildErrorIsNavigated = true;
                }
            }
            catch (Exception ex)
            {
                ex.TraceUnknownException();
            }
        }

        private void BuildEvents_OnBuildCancelled()
        {
            _viewModel.OnBuildCancelled(_buildContext);
        }

        private void BuildEvents_OnErrorRaised(object sender, BuildErrorRaisedEventArgs args)
        {
            bool buildNeedToCancel = (args.ErrorLevel == ErrorLevel.Error
                                      && _buildContext.BuildAction != vsBuildAction.vsBuildActionClean
                                      && _viewModel.ControlSettings.GeneralSettings.StopBuildAfterFirstError);
            if (buildNeedToCancel)
                _buildDistributor.CancelBuild();

            bool navigateToBuildFailureReason = (!_buildErrorIsNavigated
                                                 && args.ErrorLevel == ErrorLevel.Error
                                                 && _viewModel.ControlSettings.GeneralSettings.NavigateToBuildFailureReason == NavigateToBuildFailureReasonCondition.OnErrorRaised);
            if (navigateToBuildFailureReason)
            {
                if (args.ProjectInfo.ErrorsBox.Errors.Any(NavigateToErrorItem))
                    _buildErrorIsNavigated = true;
            }
        }

        private bool NavigateToErrorItem(ErrorItem errorItem)
        {
            if (errorItem == null || string.IsNullOrEmpty(errorItem.File) || string.IsNullOrEmpty(errorItem.ProjectFile))
                return false;

            try
            {
                var projectItem = _viewModel.FindProjectItem(errorItem.ProjectFile, FindProjectProperty.FullName);
                if (projectItem == null)
                    return false;

                var project = projectItem.StorageProject;
                if (project == null)
                    return false;

                return project.NavigateToErrorItem(errorItem);
            }
            catch (Exception ex)
            {
                ex.Trace("Navigate to error item exception");
                return true;
            }
        }

        private void ApplyToolWindowStateAction(WindowStateAction windowStateAction)
        {
            switch (windowStateAction.State)
            {
                case WindowState.Nothing:
                    break;
                case WindowState.Show:
                    _toolWindowManager.Show();
                    break;
                case WindowState.ShowNoActivate:
                    _toolWindowManager.ShowNoActivate();
                    break;
                case WindowState.Hide:
                    _toolWindowManager.Hide();
                    break;
                case WindowState.Close:
                    _toolWindowManager.Close();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }
}