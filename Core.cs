/*
**         File | Core.cs
**       Author | Ryan French
**  Description | ...
**/

using System;
using System.Text;
using System.Collections.Generic;
using Crestron.SimplSharp;
using Crestron.SimplSharp.Net.Http;
using Crestron.SimplSharp.CrestronSockets;

namespace MobotixAccess {

    public class Core {

        //===================// MEMBERS //===================//

        public string targetAddress;
        public string targetUserName;
        public string targetPassword;
        public string deviceName;

        internal HttpClient         client;
        internal HttpClientResponse response;
        internal HttpClientRequest  request;
        
        public DelegateString4 Log_Result_Callback { get; set; }

        internal string lastEventTime;

        internal CTimer pollTimer;

        public static Dictionary<string, string> userTable;

        public static List<string> mailRecipients;

        // SMTP Settings

        internal static string smtpServer;
        internal static ushort smtpPort;
        internal static string smtpUserName;
        internal static string smtpPassword;
        internal static string smtpFrom;
        internal static bool   smtpConfigured;


        //===================// CONSTRUCTOR //===================//

        public Core() {

            lastEventTime = "";
            CrestronConsole.PrintLine("MobotixAccess Core initialized!");

        }

        //===================// METHODS //===================//

        //-------------------------------------//
        //    Function | RequestLogs
        // Description | ... 
        //-------------------------------------//

        public void RequestLogs (string _address, string _userName, string _password, bool forced, bool doSendEmail) {

            try {

                client = new HttpClient();
                client.KeepAlive = false;
                client.TimeoutEnabled = true;
                client.Timeout = 6;
                client.UserName = _userName;
                client.Password = _password;

                request = new HttpClientRequest();
                request.RequestType = RequestType.Get;
                request.Header.ContentType = "text/html";

                request.Url.Parse("http://" + _address + "/admin/concierge/doplog?format=csv");
                response = client.Dispatch(request);

                if (!response.ContentString.Contains("auth method")) {
                    ErrorLog.Notice("[MOBOTIX] There was a problem fetching the logs from keypad {0}", deviceName);
                    return;
                }

                string[] log = response.ContentString.Split('\n');
                string[] entry = log[log.Length - 2].Split(',');

                string date = entry[0].Substring(1, entry[0].Length - 2);
                string timestamp = entry[1];

                // Check Unix timestamp against current time
                Int32 currentTimeUnix = (Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
                //ErrorLog.Notice("[MOBOTIX] UnixTime of Log {0}, UnixTime of Processor {1}", timestamp, currentTimeUnix);
                if (currentTimeUnix - Int32.Parse(timestamp) > 60) {

                    // Log entry more than 60 seconds old, is probably not for this event

                    if (lastEventTime != timestamp)
                        lastEventTime = timestamp;

                    ErrorLog.Notice ("[MOBOTIX] Log timestamp exceeds age threshold, discarding event on keypad {0}", deviceName);

                    return;

                }

                string action = "";
                string user = "";

                if (entry[2].Contains("Access denied")) {
                    action = "Access Denied";
                    doSendEmail = false; // Per Client: Dont send emails on fail
                } else if (entry[2].Contains("Unknown User")) {
                    action = "Access Granted";
                    user = entry[2].Substring(1, entry[2].Length - 2);
                } else {
                    action = "Access Granted";
                    user = entry[2].Substring(1, (entry[2].IndexOf('(')-2));
                }

                // If user is defined, check user against userTable to see if a proper name exists
                if (user != "") {
                    if (userTable != null) {
                        foreach (KeyValuePair<string, string> dict in userTable) {
                            if (user.Contains(dict.Key)) {
                                user = dict.Value;
                                break;
                            }
                        }
                    } else 
                        CrestronConsole.PrintLine ("Error looking up Access UserID: UserTable is Null!");
                }

                // Remove Spaces from deviceName for file handling
                string deviceNameFileSafe = deviceName.Replace(" ", "_");

                // Send if event is new event
                if (lastEventTime != timestamp || forced) {
                    if (Log_Result_Callback != null) {
                        Log_Result_Callback(date, timestamp, action, user);
                    }

                    lastEventTime = timestamp;

                    if (doSendEmail && smtpConfigured) {
                        string subject = string.Format("Mobotix - {0} code used at {1}", user, deviceName);
                        string message = string.Format("Date: {0}\nActivity: {1}{2}", date, action, user != "" ? "\nDetails: " + user + " passcode opened " + deviceName : "");

                        string to = "";
                        for (int i = 0; i < mailRecipients.Count; i++) {
                            if (i > 0)
                                to += "; ";

                            to += mailRecipients[i];
                        }

                        CrestronMailFunctions.SendMailErrorCodes result;

                        result = CrestronMailFunctions.SendMail(
                            smtpServer,
                            smtpPort,
                            smtpUserName,
                            smtpPassword,
                            smtpFrom,
                            to,
                            "",
                            subject,
                            message,
                            1,
                            "\\HTML\\mobo_" + deviceNameFileSafe + ".jpg");

                        ErrorLog.Notice("[MOBOTIX] SendMail result for keypad {0}: {1}", deviceName, result.ToString());

                        // If picture email fails, send text-only email
                        if (result != CrestronMailFunctions.SendMailErrorCodes.SMTP_OK) {

                            result = CrestronMailFunctions.SendMail(
                            smtpServer,
                            smtpPort,
                            smtpUserName,
                            smtpPassword,
                            smtpFrom,
                            to,
                            "",
                            subject,
                            message + "\n(Picture not available)",
                            0,
                            "");

                            ErrorLog.Notice("[MOBOTIX] Encountered an error sending picture email, falling back to text-only: {0}", result.ToString());

                        }

                    }

                }
                

            }
            catch (Exception exc) {

                ErrorLog.Error("Error requesting access logs from address {0}!", _address); 

            }

        }


        //-------------------------------------//
        //    Function | RequestLogs
        // Description | Overload for RequestLogs
        //-------------------------------------//

        public void RequestLogs(string _address, string _userName, string _password) {
            RequestLogs(_address, _userName, _password, true, true);
        }

        //-------------------------------------//
        //    Function | RequestLogsNoEmail
        // Description | Overload for RequestLogs
        //-------------------------------------//

        public void RequestLogsNoEmail(string _address, string _userName, string _password) {
            RequestLogs(_address, _userName, _password, true, false);
        }


        //-------------------------------------//
        //    Function | GrabCurrentImage
        // Description | Method to capture current camera image on contact closure from keypad
        //-------------------------------------//

        public void GrabCurrentImage() {

            client = new HttpClient();
            client.KeepAlive = false;
            client.TimeoutEnabled = true;
            client.Timeout = 6;
            client.UserName = targetUserName;
            client.Password = targetPassword;

            // Remove Spaces from deviceName for file handling
            string deviceNameFileSafe = deviceName.Replace(" ", "_");

            if (client.FgetFile("http://" + targetAddress + "/record/current.jpg", "\\HTML\\mobo_" + deviceNameFileSafe + ".jpg") == 0) {
                ErrorLog.Notice("[MOBOTIX] Successfully grabbed image for keypad {0}", deviceName);
            } else
                ErrorLog.Notice("[MOBOTIX] Failed to grab image for keypad {0}!", deviceName);

        }


        //-------------------------------------//
        //    Function | pollTimerHandler
        // Description | Requests access logs and resets timer
        //-------------------------------------//

        internal void pollTimerHandler(object o) {

            RequestLogs(targetAddress, targetUserName, targetPassword, false, false);
            pollTimer = new CTimer(pollTimerHandler, 2000);

        }


        //-------------------------------------//
        //    Function | InitializeUserTable
        // Description | If userTable dictionary hasn't been defined yet, define it.
        //-------------------------------------//

        public static void InitializeUserTable () {

            if (userTable == null) {
                userTable = new Dictionary<string, string>();
                CrestronConsole.PrintLine ("Initialized userTable Dictionary.");
            }

        }


        //-------------------------------------//
        //    Function | AddUserTableEntry
        // Description | Add an entry to the userTable. Called from S# modules.
        //-------------------------------------//

        public static void AddUserTableEntry (SimplSharpString userID, SimplSharpString userName) {

            userTable.Add(userID.ToString(), userName.ToString());
            CrestronConsole.PrintLine(string.Format("Added userTable entry: {0} => {1}", userID, userName));

        }


        //-------------------------------------//
        //    Function | EnablePoll
        // Description | ...
        //-------------------------------------//

        public void EnablePoll () {

            pollTimer = new CTimer(pollTimerHandler, 2000);

        }

        //-------------------------------------//
        //    Function | DisablePoll
        // Description | ...
        //-------------------------------------//

        public void DisablePoll () {

            pollTimer.Stop();
            pollTimer = null;

        }

        //-------------------------------------//
        //    Function | AddMailRecipient
        // Description | ...
        //-------------------------------------//

        public static void AddMailRecipient (SimplSharpString addr) {

            if (mailRecipients == null)
                mailRecipients = new List<string> ();

            if (addr.ToString().Contains("@") && !mailRecipients.Contains(addr.ToString())) {
                mailRecipients.Add(addr.ToString());
                CrestronConsole.PrintLine ("Added new email recipient: {0}", addr.ToString ());
            }

        }

        //-------------------------------------//
        //    Function | SetSMTPSettings
        // Description | Receive SMTP settings from S+ and store in private variables. Then set a flag indicating
        //               that email settings have been entered.
        //-------------------------------------//

        public static void SetSMTPSettings(string _server, ushort _port, string _userName, string _password, string _from) {

            smtpServer = _server;
            smtpPort = _port;
            smtpUserName = _userName;
            smtpPassword = _password;
            smtpFrom = _from;

            smtpConfigured = true;

        }

    } // End Core Class

    public delegate void DelegateString4(SimplSharpString string1, SimplSharpString string2, SimplSharpString string3, SimplSharpString string4);

}
