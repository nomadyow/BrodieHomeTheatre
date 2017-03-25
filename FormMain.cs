﻿using System;
using System.Windows.Forms;
using Microsoft.Speech.Recognition;


namespace BrodieTheatre
{
    public partial class FormMain : Form
    {
        //42.22.B8 Pot
        //42.20.F8 Tray
        //41.66.88 Motion Sensor

        static FormMain formMain;

        public DateTime GlobalShutdown;

        public int statusTickCounter = 0;

        public Random random = new Random();


        public FormMain()
        {
            hookID = SetHook(proc);
            formMain = this;
            InitializeComponent();
            resetGlobalTimer();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        private async void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Form formSettings = new FormSettings();
            formSettings.ShowDialog();

            if (currentPLMport != Properties.Settings.Default.plmPort)
            {
                currentPLMport = Properties.Settings.Default.plmPort;
                connectPLM();
            }

            if (currentHarmonyIP != Properties.Settings.Default.harmonyHubIP)
            {
                currentHarmonyIP = Properties.Settings.Default.harmonyHubIP;
                await harmonyConnectAsync();
            }
        }

        private async void FormMain_Load(object sender, EventArgs e)
        {
            writeLog("------ Starting Up ------");
            await harmonyConnectAsync(true);
            if (Program.Client != null)
            {
                Program.Client.OnActivityChanged += harmonyClient_OnActivityChanged;
            }

            currentPLMport = Properties.Settings.Default.plmPort;
            connectPLM();

            projectorConnect();

            if (labelProjectorStatus.Text == "Connected")
            {
                projectorCheckPower();
            }

            if (Properties.Settings.Default.potsAddress != "")
            {
                lights[Properties.Settings.Default.potsAddress] = -1;
            }

            if (Properties.Settings.Default.trayAddress != "")
            {
                lights[Properties.Settings.Default.trayAddress] = -1;
            }
            BeginInvoke(new Action(() =>
            {
                formMain.timerSetLights.Enabled = true;
            }
            ));
            currentHarmonyIP = Properties.Settings.Default.harmonyHubIP;
            if (Program.Client.Token != "")
            {
                BeginInvoke(new Action(() =>
                {
                    formMain.labelHarmonyStatus.Text = "Connected";
                    formMain.labelHarmonyStatus.ForeColor = System.Drawing.Color.ForestGreen;
                }
                ));
            }
            else
            {
                BeginInvoke(new Action(() =>
                {
                    formMain.labelHarmonyStatus.Text = "Disconnected";
                    formMain.labelHarmonyStatus.ForeColor = System.Drawing.Color.Maroon;
                }
                ));
            }
            BeginInvoke(new Action(() =>
            {
                formMain.recognitionEngine = new SpeechRecognitionEngine();
                formMain.recognitionEngine.SetInputToDefaultAudioDevice();
                formMain.loadVoiceCommands();
                formMain.recognitionEngine.SpeechRecognized += RecognitionEngine_SpeechRecognized;
            }
            ));
        }

        private void timerClearStatus_Tick(object sender, EventArgs e)
        {
            if (statusTickCounter > 0)
            {
                statusTickCounter -= 1;
            }
            else
            {
                toolStripStatus.Text = "";
            }     
        }

        private void toolStripStatus_TextChanged(object sender, EventArgs e)
        {
            statusTickCounter = 2;       
        }

        private void FormMain_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                if (Program.Client != null)
                {
                    Program.Client.Dispose();
                }
            }
            catch { };
            if (powerlineModem != null)
            {
                powerlineModem.Dispose();
            }
            UnhookWindowsHookEx(hookID);
            writeLog("------ Shutting Down ------");
        }

        private void timerGlobal_Tick(object sender, EventArgs e)
        {
            // Evaluate the conditions to determine if we should be counting down or not
            // for shutting down the theatre
            DateTime now = DateTime.Now;

            // Calculate when the shutdown timer was initiated based on when it will end.

            DateTime globalShutdownStart = GlobalShutdown.AddHours(Properties.Settings.Default.globalShutdown * -1);
            var totalSeconds = (GlobalShutdown - globalShutdownStart).TotalSeconds;
            var progress = (now - globalShutdownStart).TotalSeconds;

            // Reasons the timer should be ticking down if either of these is TRUE
            // - A light is on
            // - A Harmony Activity is active

            // The timer should not be active if the following are all TRUE:
            // - The lights are Off
            // - The Harmony Activity is Off
            // - The Room is vacant

            if (((labelCurrentActivity.Text != "PowerOff" && labelCurrentActivity.Text != "") || trackBarPots.Value > 0 || trackBarTray.Value > 0) && labelRoomOccupancy.Text == "Vacant")
            {
                if (GlobalShutdown > now)
                {
                    int percentage = (100 - (Convert.ToInt32((progress / totalSeconds) * 100) + 1));    
                    toolStripProgressBarGlobal.Value = percentage;
                    return;            
                }
                else
                {    
                    writeLog("Global Timer:  Sending Harmony 'PowerOff', turning off lights");
                    harmonyStartActivityByName("PowerOff");
                    toolStripProgressBarGlobal.Value = 0;
                    lightsOff();
                    return;
                }
            }
            toolStripProgressBarGlobal.Value = toolStripProgressBarGlobal.Minimum;
        }

        private void labelRoomOccupancy_TextChanged(object sender, EventArgs e)
        {
            if (labelRoomOccupancy.Text == "Occupied")
            {                
                writeLog("Occupancy:  Room Occupied");
                resetGlobalTimer();

                if (labelCurrentActivity.Text == "PowerOff" && labelKodiStatus.Text == "Stopped")
                {
                    BeginInvoke(new Action(() =>
                    {
                        lightsToEnteringLevel();
                        timerStartupSound.Enabled = true;
                        writeLog("Occupancy:  Powering On AV Amplifier");
                        harmonySendCommand(Properties.Settings.Default.occupancyDevice, Properties.Settings.Default.occupancyEnterCommand);
                    }
                    ));
                }
                recognitionEngine.RecognizeAsync(RecognizeMode.Multiple);
                labelLastVoiceCommand.Text = "Listening";
                    
                toolStripStatus.Text = "Room is now occupied";
               
            }
            else if (labelRoomOccupancy.Text == "Vacant")
            {     
                try
                {
                    // XXX move this to a timer that runs separately
                    recognitionEngine.RecognizeAsyncStop();
                    labelLastVoiceCommand.Text = "Not Listening";
                }
                catch
                {
                    writeLog("Voice:  Failed to pause Recognition Engine");
                }

                if (labelKodiStatus.Text == "Stopped")
                {
                    if (labelCurrentActivity.Text != "PowerOff")
                    {
                        // Turn off active Harmony Activity
                        harmonyStartActivityByName("PowerOff");
                    }
                    else
                    {
                        // Power off the Amplifier
                        harmonySendCommand(Properties.Settings.Default.occupancyDevice, Properties.Settings.Default.occupancyExitCommand);
                    }

                    writeLog("Occupancy:  Room Vacant");
                    toolStripStatus.Text = "Room is now vacant";
                    lightsOff();
           
                }
                else // There is playback or it is paused.  Start the timer to shut this off after configured time
                {
                    resetGlobalTimer();
                }
            }
        }

        private void resetGlobalTimer()
        {
            GlobalShutdown = DateTime.Now.AddHours(Properties.Settings.Default.globalShutdown);
            writeLog("Global Timer:  Resetting Shutdown timer");
        }

        private void labelProjectorPower_TextChanged(object sender, EventArgs e)
        {
            if (labelProjectorPower.Text == "On")
            {
                buttonProjectorChangeAspect.Enabled = true;
            }
            else
            {
                buttonProjectorChangeAspect.Enabled = false;
            }
        }

        private void labelRoomOccupancy_Click(object sender, EventArgs e)
        {
            if (labelRoomOccupancy.Text == "Occupied")
            {
                labelRoomOccupancy.Text = "Vacant";
            }
            else
            {
                labelRoomOccupancy.Text = "Occupied";
            }
        }

        private void timerStartupSound_Tick(object sender, EventArgs e)
        {
            timerStartupSound.Enabled = false;
            writeLog("Voice:  Playing startup sound");
            kodiPlayWave(startupWave);
        }
    }
}
