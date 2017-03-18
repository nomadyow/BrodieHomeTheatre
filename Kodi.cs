﻿using System;
using System.Windows.Forms;
using System.IO;


namespace BrodieTheatre
{
    public partial class FormMain : Form
    {
        private void timerKodiPoller_Tick(object sender, EventArgs e)
        {
            if (File.Exists("kodi_ar.txt"))
            {
                string kodiAspectRatio = File.ReadAllText("kodi_ar.txt").Trim().ToLower();
                File.Delete("kodi_ar.txt");
                projectorQueueChangeAspect(float.Parse(kodiAspectRatio));
            }
            if (File.Exists("kodi_status.txt"))
            {
                string kodiPlayback = File.ReadAllText("kodi_status.txt").Trim().ToLower();
                File.Delete("kodi_status.txt");
                switch (kodiPlayback)
                {
                    case "playing":
                        labelKodiStatus.Text = "Playing";
                        lightsToPlaybackLevel();
                        writeLog("Kodi:  Kodi status changed to 'Playing'");
                        resetGlobalTimer();

                        break;
                    case "stopped":
                        labelKodiStatus.Text = "Stopped";
                        lightsToStoppedLevel();
                        writeLog("Kodi:  Kodi status changed to 'Stopped'");
                        resetGlobalTimer();

                        break;
                    case "paused":
                        labelKodiStatus.Text = "Paused";
                        writeLog("Kodi:  Kodi status changed to 'Paused'");
                        lightsToPausedLevel();

                        resetGlobalTimer();

                        break;
                    default:
                        labelKodiStatus.Text = "Stopped";
                        writeLog("Kodi:  Unknown Kodi status - assuming 'Stopped'");
                        lightsToStoppedLevel();

                        resetGlobalTimer();

                        break;
                }
            }
        }
    }
}