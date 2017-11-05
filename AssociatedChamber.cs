using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Timers;

namespace CRONIN
{
    //--------------------------------------------------
    // Class for controlling a temperature chamber manufactured by Associated Environmental Systems
    //--------------------------------------------------
    public class AssociatedChamber
	{
        //------public delegates (function pointers)-----------------------------------
        public delegate void TemperatureNotificationDelegate(int iTemp);
        public delegate void TemperatureTimeoutDelegate(int iDesiredTemp, int iActualTemp);
        public delegate void LogDelegate(string sLogInfo);

        //------Events-----------------------------------
        public event TemperatureNotificationDelegate eventTempReached;
		public event TemperatureTimeoutDelegate eventTimeout;

		//------public consts-----------------------------------
		public const int MIN_TEMP = -60;     // Degrees C
		public const int MAX_TEMP = 80;      // Degrees C
		public const int TEMP_TOLERANCE = 1; // Degrees C
		//
		public const int HEAT_UPON_COMPLETION_THRESHOLD_TEMP = 5; // Degrees C
		public const int ROOM_TEMPERATURE = 23; // Degrees C
		public const int DEFAULT_TEST_TEMP = 35; // Degrees C
		public const int DEFAULT_COMPLETION_TEMP = ROOM_TEMPERATURE; 
		//
		public const bool NOTIFY_WHEN_DONE = true;
		public const bool NOTIFY_OFF = false;
		public const int WAIT_FOREVER = 0;
		//
		public const int DEFAULT_COM_PORT = 9;
		public const int DEFAULT_BAUD_RATE = 9600;


        //------private consts-----------------------------------
        private const int SECONDS_PER_MINUTE = 60;
        private const int POLL_CHAMBER_INTERVAL_IN_SECONDS = 2;
		private const double POLL_CHAMBER_INTERVAL_IN_MILLISECONDS = 1000 * POLL_CHAMBER_INTERVAL_IN_SECONDS; 
		private const int REGISTER_TEMPERATURE = 300;
		private const int REGISTER_PURGE_VALVE = 2000;
 
		//------class variables ("fields")----------------------------------
		private bool mbConnected;
		private bool mbIsSerialPortOpen;
		private int miDesiredTemp;
		private System.Timers.Timer timerPollChamber;
		private int miTimeoutInMinutes;
		private int miTimeoutCounterInSeconds;
		private SerialPort moSerialPort;
		private LogDelegate mpLog; // This will hold our log-file delegate     
     
		//------Constructor(s)-----------------------------------
        //
		public AssociatedChamber(bool bConnected, LogDelegate methodLog)
		{
            //----------------
            // Parameters:
            //----------------
            // bConnected:  TRUE  = We want to have a "real" connection to a chamber
            //              FALSE = Not actually connected (use for testing when we're not physically connected to the chamber)
            //
            // methodLog:   The higher-level log function that we can utilize so that we can log messages from this class
            //
            //
            // Init fields:
            mbConnected = bConnected;
			mbIsSerialPortOpen = false;
			miDesiredTemp = 0;
			miTimeoutInMinutes = 0;
			miTimeoutCounterInSeconds = 0;
			mpLog += methodLog;            

			// Timer init:
			timerPollChamber = new System.Timers.Timer(POLL_CHAMBER_INTERVAL_IN_MILLISECONDS);
			timerPollChamber.Enabled = false;
			timerPollChamber.Elapsed += new ElapsedEventHandler(this.timerPollChamber_OnElapsed);
		}

		//------Properties-----------------------------------
		public bool IsConnected { get { return mbConnected; } }
		public bool IsSerialPortOpen { get { return mbIsSerialPortOpen; } }

		//------Methods-----------------------------------
		//-------------------------------------------------------------------------------
		public void OpenSerialPort(int iPortNum, int iBaudRate)
		{
			if (!mbConnected)
			{
				mbIsSerialPortOpen = true;
				//----------------------------
				return;
				//----------------------------
			}

			if (moSerialPort == null)
				moSerialPort = new SerialPort();
			else if (mbIsSerialPortOpen)
				CloseSerialPort();

			moSerialPort.PortName = "COM" + iPortNum.ToString();
			moSerialPort.BaudRate = iBaudRate;

			// Go ahead and open it, this will throw an error if there are problems...
			moSerialPort.Open();

			// Now check the status...
			if (!moSerialPort.IsOpen)
				throw new Exception("Problem opening chamber serial port");

			mbIsSerialPortOpen = true;

		} // OpenSerialPort

		//-------------------------------------------------------------------------------
		public void CloseSerialPort()
		{
			if ( mbIsSerialPortOpen )
			{
				if (mbConnected)
					moSerialPort.Close();
				//
				mbIsSerialPortOpen = false;
			}
			// else
			//     Already closed!
		} // CloseSerialPort

		//-------------------------------------------------------------------------------
		public void PingUntilAwake(int iTimeoutInMilliseconds)
		{
			// Call this function after turning on the chamber.
			//		Possible outcomes:
			//		(a) Return from the function when the chamber "wakes up"
			// or   (b) Throw an error after the timeout is reached

			const int SLEEP_INTERVAL_IN_MILLISECONDS = 250;

			int iTimeoutCounterInMilliseconds = 0;
			bool bDone = false;

			while (!bDone)
			{
				try
				{
					GetTemp();

					// If we got this far (no exception) then we're good!
					bDone = true;
				}
				catch (Exception oExcept)
				{
					if  ( iTimeoutCounterInMilliseconds < iTimeoutInMilliseconds ) 
					{
						// Sleep and then try again:
						Thread.Sleep( SLEEP_INTERVAL_IN_MILLISECONDS);
						iTimeoutCounterInMilliseconds += SLEEP_INTERVAL_IN_MILLISECONDS;
					}
					else
						// Doh!
						throw new Exception("Unable to communicate with chamber after waiting " + iTimeoutInMilliseconds.ToString()
					                       + " milliseconds.\n\n" + "If powering via control box, make sure control box is on and"
										   + " at least one camera is connected.\n\n" + "Last returned error: " + oExcept.Message);
				}
			}

		} // PingUntilAwake()

		//-------------------------------------------------------------------------------
		public void SetTemp(int iTemp, bool bTurnNotifyOn, int iTimeoutInMinutes)
		{
            //Parameters:
            //
            // iTemp             = new chamber set point
            // bTurnNotifyOn     = TRUE if you want this class to notify you when temperature is reached
            // iTimeoutInMinutes = Timeout waiting for chamber to reach temperature

            bool bSuccess = false;
			int iTryCount = 0;

			if ( (iTemp < MIN_TEMP) || (iTemp > MAX_TEMP) )
				throw new Exception("Requested temp (" + iTemp.ToString() + ") is beyond range of chamber capability");
			//
			// Kill timer, in case its already running:
			timerPollChamber.Enabled = false;
			//
			if (mbConnected)
			{
				while (!bSuccess)
				{
					try
					{
						this.SendChamberCmd(REGISTER_TEMPERATURE, iTemp);

						// If we got here, an error did not occur:
						bSuccess = true;
					}
					catch (Exception oExcept)
					{
						// Increment try-count and try again:
						iTryCount++;

						if (iTryCount == 3)
							throw new Exception("Unable to set chamber temp after " + iTryCount.ToString() 
												+ " tries. Error: " + oExcept.Message);
					}
				}
			}
			miDesiredTemp = iTemp;
			//
			if (bTurnNotifyOn)
			{
				// 1) Reset timeout counter:
				miTimeoutInMinutes = iTimeoutInMinutes;
				miTimeoutCounterInSeconds = 0;
				//
				// 2) Turn on timer to periodically poll chamber temp:
				timerPollChamber.Enabled = true;
			}
		} // SetTemp

		//-------------------------------------------------------------------------------
		public int GetTemp()
		{
			if (!mbIsSerialPortOpen)
				throw new Exception("Chamber Serial port not open");

			if (!mbConnected)
				// Not connected, just return desired temp:
				return miDesiredTemp;

			byte[] nSndBuffer = new byte[32];
			int nSndBufferIndex = 0;    //index of empty cell
			byte[] nRcvBuffer = new byte[32];
			int nRcvBufferIndex = 0;    //index of empty cell
			const int nMaxRetry = 3;
			const int nTempMsgSize = 7;
			//int iRetVal;
			sbyte signedByte;

			//Write get temp command
			Array.Copy(new byte[] { 1, 3, 0, 100, 0, 1, 197, 213 }, 0, nSndBuffer, 0, 8);
			nSndBufferIndex += 8;
			moSerialPort.DiscardInBuffer();
			moSerialPort.Write(nSndBuffer, 0, nSndBufferIndex);

			Thread.Sleep(100);

			//Read temp
			nRcvBufferIndex += moSerialPort.Read(nRcvBuffer, nRcvBufferIndex, moSerialPort.BytesToRead);
			int nRtyIndex = 0;
			while ((nRtyIndex < nMaxRetry) & (nRcvBufferIndex < nTempMsgSize))
			{
				Thread.Sleep(200);
				nRcvBufferIndex += moSerialPort.Read(nRcvBuffer, nRcvBufferIndex, moSerialPort.BytesToRead);
				nRtyIndex++;
			}

			// temperature message not recieved
			if ((nRtyIndex >= nMaxRetry) & (nRcvBufferIndex < nTempMsgSize))
				throw new Exception("Chamber temp Return Msg not received.  Max Read Retry Exceded");

			// temperature message too long
			if (nRcvBufferIndex > nTempMsgSize)
				throw new Exception("Chamber temp Return exceed msg length");

			//Check Proper temperature message
			if (nRcvBufferIndex != nTempMsgSize)
				throw new Exception("Chamber temp Return Msg too short");

			// Buffer 4 contains our signed 8-bit temperature value:
			signedByte = (sbyte)nRcvBuffer[4];

			//-------------
			return (int)signedByte;
			//-------------
		} // GetTemp()
              
		//-------------------------------------------------------------------------------
		public void OpenPurgeValve()
		{
			this.SendChamberCmd( REGISTER_PURGE_VALVE, 1);

		} // OpenPurgeValve

		//-------------------------------------------------------------------------------
		public void ClosePurgeValve()
		{
			this.SendChamberCmd( REGISTER_PURGE_VALVE, 0);

		} // ClosePurgeValve
		
		//-------------------------------------------------------------------------------
		private void SendChamberCmd(int nRegister, int nSetValue)
		{
			if (!mbIsSerialPortOpen)
				throw new Exception("Chamber Serial port not open");

			if (!mbConnected)
				//---------
				return;
				//---------

			byte[] nSndBuffer = new byte[32];
			int nSndBufferIndex = 0;    //index of empty cell
			byte[] nRcvBuffer = new byte[32];
			int nRcvBufferIndex = 0;    //index of empty cell
			byte[] nTempBuffer;
			int nCmdCRC;
			const int nMaxRetry = 3;

			if (nSetValue < 0)
			{//get two's complement for negative numbers
				nSetValue = (Math.Abs(nSetValue) ^ 65535) + 1;
			}
			//build command
			Array.Copy(new byte[] { 1, 6, (byte)(nRegister >> 8), (byte)nRegister, (byte)(nSetValue >> 8), (byte)nSetValue }, 0, nSndBuffer, nSndBufferIndex, 6);
			nSndBufferIndex += 6;

			//compute and add checksum
			nTempBuffer = new byte[nSndBufferIndex];
			Array.Copy(nSndBuffer, 0, nTempBuffer, 0, nSndBufferIndex);
			nCmdCRC = getCRC(nTempBuffer);
			Array.Copy(new byte[] { (byte)nCmdCRC, (byte)(nCmdCRC >> 8) }, 0, nSndBuffer, nSndBufferIndex, 2);
			nSndBufferIndex += 2;

			//Write message
			moSerialPort.DiscardInBuffer();
			moSerialPort.Write(nSndBuffer, 0, nSndBufferIndex);
			Thread.Sleep(100);

			//Read back echo
			nRcvBufferIndex += moSerialPort.Read(nRcvBuffer, nRcvBufferIndex, moSerialPort.BytesToRead);
			int nRtyIndex = 0;
			while ((nRtyIndex < nMaxRetry) & (nRcvBufferIndex < nSndBufferIndex))
			{
				Thread.Sleep(200);
				nRcvBufferIndex += moSerialPort.Read(nRcvBuffer, nRcvBufferIndex, moSerialPort.BytesToRead);
				nRtyIndex++;
			}

			// Echo message not recieved
			if ((nRtyIndex >= nMaxRetry) & (nRcvBufferIndex < nSndBufferIndex))
			{
				throw new Exception("Chamber msg echo not received.  Max Read Retry Exceded");
			}
			// Echo message too long
			if (nRcvBufferIndex > nSndBufferIndex)
			{
				throw new Exception("Chamber echo exceed msg length");
			}
			//Check Proper echo
			if (nRcvBufferIndex == nSndBufferIndex)
			{
				byte[] nRcvData = new byte[nSndBufferIndex];
				Array.Copy(nRcvBuffer, 0, nRcvData, 0, nRcvBufferIndex);
				nTempBuffer = null;
				nTempBuffer = new byte[nRcvBufferIndex];
				Array.Copy(nSndBuffer, nTempBuffer, nRcvBufferIndex);
				//Compare message sent with message received
				if (ArraysAreEqual(nTempBuffer, nRcvData))
				{
					// Good, we're done!!!!!
					//---------
					return;
					//---------
				}
				else
				{
					throw new Exception("Chamber echo Msg didn't match");
				}
			}
			throw new Exception("Chamber echo Msg too short");
		} // SendChamberCmd

		//-------------------------------------------------------------------------------
		private int getCRC(byte[] nCMD)
		{

			int poly = int.Parse("A001", System.Globalization.NumberStyles.HexNumber);
			int nCRCINIT;
			int nCRC;
			nCRCINIT = int.Parse("FFFF", System.Globalization.NumberStyles.HexNumber);
			nCRC = int.Parse("FFFF", System.Globalization.NumberStyles.HexNumber);

			for (int i = 0; i < 6; i++)
			{
				nCRC = nCRC ^ (int)nCMD[i];
				for (int j = 0; j < 8; j++)
				{
					if ((nCRC & 1) == 1)
					{
						nCRC = nCRC >> 1;
						nCRC = nCRC ^ poly;
					}
					else
					{
						nCRC = nCRC >> 1;
					}
				}
			}
			return nCRC;
		} // getCRC

		//-------------------------------------------------------------------------------
		private bool ArraysAreEqual(byte[] ba1, byte[] ba2)
		{
			if (ba1 == null)
			{
				return false;
			}
			if (ba2 == null)
			{
				return false;
			}
			if (ba1.Length != ba2.Length)
			{
				return false;
			}

			for (int i = 0; i < ba1.Length; i++)
			{
				if (ba1[i] != ba2[i])
				{
					return false;
				}
			}
			return true;
		} // ArraysAreEqual

		//-------------------------------------------------------------------------------
		private void timerPollChamber_OnElapsed(object oSource, ElapsedEventArgs oArgs)
		{
            // When this event fires, we can see if we've reached our desired temperature...

			int iActualTemp;
			int iTempDifference;
			int iElapsedTimeInMinutes;

			try 
			{
				iActualTemp = GetTemp();
				//
				iTempDifference = Math.Abs(iActualTemp - miDesiredTemp);
				//
				if (iTempDifference <= TEMP_TOLERANCE)
				{
					// We have reached desired temp!  Turn off timer and raise eventTempReached:
					timerPollChamber.Enabled = false;
					//
					if (this.eventTempReached != null)
						this.eventTempReached(iActualTemp);
				}
				else
				{
					// We haven't reached our temp yet.  Increment our counter and 
					//		check to see if we've timed-out:
					miTimeoutCounterInSeconds += POLL_CHAMBER_INTERVAL_IN_SECONDS;
					//
					// First just report the temp every 60 seconds:
					if ((miTimeoutCounterInSeconds % SECONDS_PER_MINUTE) == 0)
					{
						//iActualTemp = GetTemp(true);
						mpLog("Current chamber temp: " + iActualTemp.ToString() + " (Desired: " + miDesiredTemp.ToString() + ")");
					}
					//-------------------------
					// Now check for timeout:
					//-------------------------
					if (miTimeoutInMinutes != WAIT_FOREVER)
					{
						iElapsedTimeInMinutes = miTimeoutCounterInSeconds / SECONDS_PER_MINUTE;
						//
						if (iElapsedTimeInMinutes >= miTimeoutInMinutes)
						{
							// We have timed-out!  Turn off timer and raise eventTimeout:
							timerPollChamber.Enabled = false;
							//
							if (this.eventTimeout != null)
								this.eventTimeout(miDesiredTemp, iActualTemp);
						}
					}
				}
			}
			catch(Exception oExcept) 
			{
				// Ugh.
				mpLog("Exception in timerPollChamber_OnElapsed: " + oExcept.Message);
			}
		} // timerPollChamber_OnElapsed

    } // class AssociatedChamber

} // namespace CRONIN
