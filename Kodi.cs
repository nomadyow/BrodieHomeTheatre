﻿using System;
using System.IO;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;


namespace BrodieTheatre
{
    public partial class FormMain : Form
    {
        public MovieEntry kodiPlayNext = null;
        public TcpClient tcpClient;
        public NetworkStream kodiSocketStream;
        public StreamReader kodiStreamReader;
        public StreamWriter kodiStreamWriter;
        public char[] kodiReadBuffer = new char[1000000];
        public int kodiReadBufferPos = 0;
        public class MovieEntry
        {
            public string file { get; set; }
            public string name { get; set; }
            public string cleanName { get; set; }
            public List<string> shortNames = new List<string>();
        }
        public List<MovieEntry> kodiMovies = new List<MovieEntry>();

        public class PartialMovieEntry
        {
            public string file { get; set; }
            public string name { get; set; }
        }

        public List<PartialMovieEntry> moviesFullNames = new List<PartialMovieEntry>();
        public List<PartialMovieEntry> moviesAfterColonNames = new List<PartialMovieEntry>();
        public List<PartialMovieEntry> moviesPartialNames = new List<PartialMovieEntry>();
        public List<PartialMovieEntry> moviesDuplicateNames = new List<PartialMovieEntry>();
        public bool kodiLoadingMovies = false;


        private void kodiConnect()
        {
            writeLog("Kodi:  Connecting");
            kodiStatusDisconnect(false);
            tcpClient = new TcpClient();
            tcpClient.ReceiveTimeout = 500;
            var result = tcpClient.BeginConnect(Properties.Settings.Default.kodiIP, Properties.Settings.Default.kodiJSONPort, null, null);
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            if (success)
            {
                kodiSocketStream = tcpClient.GetStream();
                kodiStreamReader = new StreamReader(kodiSocketStream);
                kodiStreamWriter = new StreamWriter(kodiSocketStream);
                kodiSocketStream.Flush();
                Thread thread = new Thread(kodiReadStream);
                thread.Start();
                writeLog("Kodi:  Connected");
                labelKodiStatus.Text = "Connected";
                labelKodiStatus.ForeColor = System.Drawing.Color.ForestGreen;
                kodiSendGetMoviesRequest();
            }
            else
            {
                kodiStatusDisconnect();
            }
        }

        public async void kodiReadStream()
        {
            char[] buffer = new char[1000];
            //int bytesRead = 0;
            bool ended = false;
            while (!ended)
            {
                try
                {
                    int bytesRead = await kodiStreamReader.ReadAsync(buffer, 0, 1000);
                    Array.Copy(buffer, 0, kodiReadBuffer, kodiReadBufferPos, bytesRead);
                    kodiReadBufferPos += bytesRead;
                    formMain.BeginInvoke(new Action(() =>
                    {
                        formMain.kodiFindJson();
                    }
                    ));
                }
                catch
                {
                    ended = true;
                }
            }
            formMain.BeginInvoke(new Action(() =>
            {
                formMain.writeLog("Kodi:  Exiting JSON read thread");
            }
            ));
        }

        public void kodiFindJson()
        {
            int braces = 0;
            bool inQ = false;
            char lastB = ' ';

            int curPos = 0;
            int startPos = 0;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            while (curPos < kodiReadBufferPos)
            {
                char b = kodiReadBuffer[curPos];
                curPos += 1;
                sb.Append(b);

                if (b == '"' && lastB != '\\')
                {
                    inQ = !inQ;
                }
                else if (b == '{' && !inQ)
                {
                    braces += 1;
                }
                else if (b == '}' && !inQ)
                {
                    braces -= 1;
                }
                lastB = (char)b;
                if (braces == 0)
                {

                    int newBufferLength = kodiReadBufferPos - curPos;
                    string currJson = sb.ToString();
                    sb = new System.Text.StringBuilder();
                    formMain.BeginInvoke(new Action(() =>
                    {
                        formMain.kodiProcessJson(currJson);
                    }
                    ));

                    startPos = curPos;
                }
            }
            if (braces > 0)
            {
                int newBufferLength = kodiReadBufferPos - startPos;
                Array.Copy(kodiReadBuffer, startPos, kodiReadBuffer, 0, newBufferLength);
                kodiReadBufferPos = newBufferLength;
            }
            else
            {
                kodiReadBufferPos = 0;
            }
        }

        public void kodiProcessJson(string jsonText)
        {
            Dictionary<string, dynamic > result = JsonConvert.DeserializeObject<Dictionary<string, dynamic>>(jsonText);
            if (result.ContainsKey("method"))
            {
                switch (result["method"])
                {
                    case "Player.OnPause":
                        writeLog("Kodi:  Kodi status changed to 'Paused'");
                        labelKodiPlaybackStatus.Text = "Paused";
                        lightsToPausedLevel();
                        resetGlobalTimer();
                        break;
                    case "Player.OnPlay":
                        writeLog("Kodi:  Kodi status changed to 'Playing'");
                        labelKodiPlaybackStatus.Text = "Playing";
                        lightsToPlaybackLevel();
                        resetGlobalTimer();
                        break;
                    case "Player.OnStop":
                        writeLog("Kodi:  Kodi status changed to 'Stopped'");
                        labelKodiPlaybackStatus.Text = "Stopped";
                        lightsToStoppedLevel();
                        resetGlobalTimer();
                        break;
                    case "System.OnQuit":
                        writeLog("Kodi:  Kodi is exiting");
                        kodiStatusDisconnect();
                        break;
                    case "VideoLibrary:OnScanStarted":
                        writeLog("Kodi:  Kodi library updated");
                        kodiSendGetMoviesRequest();
                        break;
                    case "Other.aspectratio":
                        if (result["params"]["sender"] == "brodietheatre")
                        {
                            string kodiAspectRatio = result["params"]["data"];
                            projectorQueueChangeAspect(float.Parse(kodiAspectRatio));
                        }
                        break;
                }
            }
            else if (result.ContainsKey("result") && result["result"].GetType() == typeof(string))
            {
                writeLog("Kodi:  Received JSON " + jsonText);
            }
            else if(result.ContainsKey("result") && result["result"]["movies"] != null)
            {
                writeLog("Kodi:  Received list of movies");
                kodiLoadingMovies = true;
                kodiMovies.Clear();
                int movieCounter = 0;

                moviesFullNames.Clear();
                moviesAfterColonNames.Clear();
                moviesDuplicateNames.Clear();
                moviesPartialNames.Clear();
                

                foreach (JObject movie in result["result"]["movies"])
                {            
                    MovieEntry movieEntry = new MovieEntry();
                    movieEntry.file = movie["file"].ToString();
                    movieEntry.name = movie["label"].ToString();
                    string cleanName = cleanString(movieEntry.name);
                    movieEntry.cleanName = cleanName;
                    kodiMovies.Add(movieEntry);
                    PartialMovieEntry tempEntry = new PartialMovieEntry();
                    tempEntry.file = movieEntry.file;
                    tempEntry.name = cleanName;
                    moviesFullNames.Add(tempEntry);

                    // Some movies are more easily known by the part that comes after the : in a title
                    // For Example:  The Lord of the Rings: The Fellowship of the Ring
                    if ( movieEntry.name.Contains(":"))
                    {
                        string[] splitted = movieEntry.name.Split(new char[] { ':' }, 2);
                        string afterColon = cleanString(splitted[1]);
                        
                        if (! searchMovieList(moviesFullNames, afterColon))
                        {
                            PartialMovieEntry tempColonEntry = new PartialMovieEntry();
                            tempColonEntry.file = movieEntry.file;
                            tempColonEntry.name = afterColon;
                            if (searchMovieList(moviesAfterColonNames, afterColon))
                            {
                                moviesDuplicateNames.Add(tempColonEntry);
                            }
                            else
                            {
                                moviesAfterColonNames.Add(tempColonEntry);
                            }
                        }

                    }
                    List<string> cutNames = getShortMovieTitles(cleanName);
                    foreach (string partName in cutNames)
                    {
                        PartialMovieEntry tempPrefixEntry = new PartialMovieEntry();
                        tempPrefixEntry.file = movieEntry.file;
                        tempPrefixEntry.name = partName;
                        if (searchMovieList(moviesPartialNames, partName))
                        {
                            moviesDuplicateNames.Add(tempPrefixEntry);
                        }
                        else
                        {
                            moviesPartialNames.Add(tempPrefixEntry);
                        }
                        
                    }
                    movieCounter += 1;
                }
                toolStripStatus.Text = "Kodi movie list updated: " + movieCounter.ToString() + " movies";
                kodiLoadingMovies = false;
                //loadVoiceCommands();
                Task task = Task.Run((Action)loadVoiceCommands);
            }
            else
            {
                writeLog("Kodi:  Received unknown JSON:  " + jsonText);
            }
        }
        public bool searchMovieList(List<PartialMovieEntry> theList, string searchTerm)
        {
            bool found = false;
            foreach (PartialMovieEntry entry in theList)
            {
                if (entry.name == searchTerm)
                {
                    return true;
                }
            }
            return found;
        }
        public List<string> getShortMovieTitles(string name)
        {
            List<string> nameList = new List<string>();
            bool ended = false;
            int startPos = 0;
            while (! ended)
            {
                int firstPos = name.IndexOf(" ", startPos);
                startPos = firstPos + 1;
                if (firstPos > 0)
                {
                    string cut = name.Substring(0, firstPos);
                    nameList.Add(cut);
                }
                else
                {
                    ended = true;
                }
                if (startPos >= name.Length)
                {
                    ended = true;
                }
                
            }

            return nameList;
        }

        public string cleanString(string name)
        {

            string newName = Regex.Replace(name, @"\&", " and ");
            newName = Regex.Replace(newName, @"[^a-zA-Z0-9\s\']", " ");
            newName = Regex.Replace(newName, @"\s+", " ");
            return newName;
        }

        public void kodiSendJson(string command)
        {
            try
            {
                kodiStreamWriter.WriteLine(command);
                kodiStreamWriter.Flush();
            }
            catch (IOException)
            {
                timerKodiConnect.Enabled = true;
            }
            catch (NullReferenceException) { }
        }

        public void kodiStatusDisconnect(bool enableTimer = true)
        {
            labelKodiStatus.Text = "Disconnected";
            labelKodiStatus.ForeColor = System.Drawing.Color.Maroon;
            timerKodiConnect.Enabled = enableTimer;
        }

        public void kodiSendGetMoviesRequest()
        {
            writeLog("Kodi:  Request movie list");
            kodiSendJson("{\"jsonrpc\": \"2.0\", \"method\": \"VideoLibrary.GetMovies\", \"params\": { \"properties\" : [\"file\"] }, \"id\": \"1\"}");
        }

        private void kodiPlaybackControl(string command, string media=null)
        {
            // It seems the Active Player is always "1".  Use this if we need to query it.
            //  "{\"jsonrpc\": \"2.0\", \"method\": \"Player.GetActivePlayers\", \"id\": \"1\"}"
            switch (command)
            {
                case "Pause":
                    kodiSendJson("{\"jsonrpc\": \"2.0\", \"method\": \"Player.PlayPause\", \"params\": { \"playerid\" : 1 }, \"id\": \"1\"}");
                    break;
                case "Play":
                    kodiSendJson("{\"jsonrpc\": \"2.0\", \"method\": \"Player.PlayPause\", \"params\": { \"playerid\" : 1 }, \"id\": \"1\"}");
                    break;
                case "Stop":
                    kodiSendJson("{\"jsonrpc\": \"2.0\", \"method\": \"Player.Stop\", \"params\": { \"playerid\" : 1 }, \"id\": \"1\"}");
                    break;
            }
        }
    }
}