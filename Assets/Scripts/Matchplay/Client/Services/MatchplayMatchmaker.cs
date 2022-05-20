using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Matchmaker;
using Unity.Services.Matchmaker.Models;
using Matchplay.Shared;

namespace Matchplay.Client
{
    public enum MatchmakerPollingResult
    {
        Success,
        TicketCreationError,
        TicketCancellationError,
        TicketRetrievalError,
        MatchAssignmentError
    }

    public class MatchmakingResult
    {
        public string ip;
        public int port;
        public MatchmakerPollingResult result;
        public string resultMessage;
    }

    public class MatchplayMatchmaker : IDisposable
    {
        string m_LastUsedTicket;
        bool m_IsMatchmaking = false;

        //Game mode Data Path for the matchmaker.
        const string k_ModeRuleName = "game_mode";

        //gameMap Data Path for the matchmaker.
        const string k_MapRuleName = "game_map";

        CancellationTokenSource m_CancelToken;
        const int k_GetTicketCooldown = 1000;

        /// <summary>
        /// Create a ticket for the one user and begin matchmaking with their preferences
        /// </summary>
        /// <param name="data">The Client's preferences and ID</param>
        public async Task<MatchmakingResult> Matchmake(UserData data)
        {
            m_CancelToken = new CancellationTokenSource();
            var createTicketOptions = UserDataToTicketRuleOptions(data);
            var players = new List<Player>
            {
                new Player(data.userAuthId, data.userGamePreferences)
            };
            try
            {
                m_IsMatchmaking = true;
                var createResult = await MatchmakerService.Instance.CreateTicketAsync(players, createTicketOptions);
                m_LastUsedTicket = createResult.Id;
                try
                {
                    //Polling Loop, cancelling should take us all the way to the method
                    while (!m_CancelToken.IsCancellationRequested)
                    {
                        var checkTicket = await MatchmakerService.Instance.GetTicketAsync(m_LastUsedTicket);

                        if (checkTicket.Type == typeof(MultiplayAssignment))
                        {
                            var matchAssignment = (MultiplayAssignment)checkTicket.Value;

                            if (matchAssignment.Status == MultiplayAssignment.StatusOptions.Found)
                                return ReturnMatchResult(MatchmakerPollingResult.Success, "", matchAssignment);

                            if (matchAssignment.Status == MultiplayAssignment.StatusOptions.Timeout ||
                                matchAssignment.Status == MultiplayAssignment.StatusOptions.Failed)
                                return ReturnMatchResult(MatchmakerPollingResult.MatchAssignmentError,
                                    $"Ticket: {m_LastUsedTicket} - {matchAssignment.Status} - {matchAssignment.Message}");

                            Debug.Log($"Polled Ticket: {m_LastUsedTicket} Status: {matchAssignment.Status} ");
                        }

                        await Task.Delay(k_GetTicketCooldown);
                    }
                }
                catch (MatchmakerServiceException e)
                {
                    return ReturnMatchResult(MatchmakerPollingResult.TicketRetrievalError, e.ToString());
                }
            }
            catch (MatchmakerServiceException e)
            {
                return ReturnMatchResult(MatchmakerPollingResult.TicketCreationError, e.ToString());
            }

            return ReturnMatchResult(MatchmakerPollingResult.TicketCancellationError, "Cancelled Matchmaking");
        }

        public bool IsMatchmaking => m_IsMatchmaking;

        public MatchplayMatchmaker()
        {
            try
            {
                // SetStagingEnvironment(); for internal unity testing only
            }
            catch (Exception ex)
            {
                Debug.Log(ex);
            }
        }

        public async Task CancelMatchmaking()
        {
            if (!m_IsMatchmaking)
                return;
            m_IsMatchmaking = false;
            if (m_CancelToken.Token.CanBeCanceled)
                m_CancelToken.Cancel();

            if (string.IsNullOrEmpty(m_LastUsedTicket))
                return;

            Debug.Log($"Cancelling {m_LastUsedTicket}");
            await MatchmakerService.Instance.DeleteTicketAsync(m_LastUsedTicket);
        }

        //Make sure we exit the matchmaking cycle through this method every time.
        MatchmakingResult ReturnMatchResult(MatchmakerPollingResult resultErrorType, string message = "",
            MultiplayAssignment assignment = null)
        {
            m_IsMatchmaking = false;

            if (assignment != null)
            {
                var parsedIP = assignment.Ip;
                var parsedPort = assignment.Port;
                if (parsedPort == null)
                    return new MatchmakingResult
                    {
                        result = MatchmakerPollingResult.MatchAssignmentError,
                        resultMessage = $"Port missing? - {assignment.Port}\n-{assignment.Message}"
                    };

                return new MatchmakingResult
                {
                    result = MatchmakerPollingResult.Success,
                    ip = parsedIP,
                    port = (int)parsedPort,
                    resultMessage = assignment.Message
                };
            }

            return new MatchmakingResult
            {
                result = resultErrorType,
                resultMessage = message
            };
        }

        /// <summary>
        /// Testing environment
        /// </summary>
        async void SetStagingEnvironment()
        {
            await AuthenticationWrapper.Authenticating();
            var sdkConfiguration = (IMatchmakerSdkConfiguration)MatchmakerService.Instance;
            sdkConfiguration.SetBasePath("https://matchmaker-stg.services.api.unity.com");
            Debug.LogWarning("CAUTION: Setting Matchmaker environment to Staging!");
        }

        /// <summary>
        /// From Game player to matchmaking player
        /// </summary>
        static CreateTicketOptions UserDataToTicketRuleOptions(UserData data)
        {
            //TODO Come up with a good use case for attributes (Skill?)
            var attributes = new Dictionary<string, object>
            {
                //Moved these to Player Preferences
                //{ k_ModeRuleName, data.userGamePreferences.ModeRules()},
               // { k_MapRuleName, data.userGamePreferences.MapRules() }
            };

            var queueName = data.userGamePreferences.ToMultiplayQueue();

            return new CreateTicketOptions(queueName, attributes);
        }

        public void Dispose()
        {
#pragma warning disable 4014
            CancelMatchmaking();
#pragma warning restore 4014
            m_CancelToken?.Dispose();
        }
    }
}