﻿#region CmdMessenger - MIT - (c) 2014 Thijs Elenbaas.
/*
  CmdMessenger - library that provides command based messaging

  Permission is hereby granted, free of charge, to any person obtaining
  a copy of this software and associated documentation files (the
  "Software"), to deal in the Software without restriction, including
  without limitation the rights to use, copy, modify, merge, publish,
  distribute, sublicense, and/or sell copies of the Software, and to
  permit persons to whom the Software is furnished to do so, subject to
  the following conditions:

  The above copyright notice and this permission notice shall be
  included in all copies or substantial portions of the Software.

  Copyright 2014 - Thijs Elenbaas
*/
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using InTheHand.Net;
using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace CommandMessenger.Bluetooth
{

   
    /// <summary>
    /// Class for storing last succesful connection
    /// </summary>
    [Serializable]
    public class BluetoothConfiguration 
    {
        public BluetoothAddress BluetoothAddress { get; set; }
        public Dictionary<BluetoothAddress, string> StoredDevicePins { get; set; }

        public BluetoothConfiguration()
        {
            StoredDevicePins = new Dictionary<BluetoothAddress, string>();
        }
    }

    /// <summary>
    /// Connection manager for Bluetooth devices
    /// </summary>
    public class BluetoothConnectionManager : ConnectionManager
    {
        private static readonly List<string> CommonDevicePins = new List<string>
            {                
                "0000",                
                "1111",
                "1234",
            };

        private enum ScanType { None, Quick, Thorough }

        const string SettingsFileName = @"LastConnectedBluetoothSetting.cfg";
        private BluetoothConfiguration _bluetoothConfiguration;
        private readonly BluetoothTransport _bluetoothTransport;
        private ScanType _scanType;
        //private bool _activeConnection;

        // The control to invoke the callback on
        private readonly object _tryConnectionLock = new object();
        private readonly List<BluetoothDeviceInfo> _deviceList;
        private List<BluetoothDeviceInfo> _prevDeviceList;

        /// <summary>
        /// Connection manager for Bluetooth devices
        /// </summary>
        public BluetoothConnectionManager(BluetoothTransport bluetoothTransport, CmdMessenger cmdMessenger, int watchdogCommandId = 0, string uniqueDeviceId = null) :
            base(cmdMessenger, watchdogCommandId, uniqueDeviceId)
        {
            if (bluetoothTransport == null) 
                throw new ArgumentNullException("bluetoothTransport", "Transport is null.");

            _bluetoothTransport = bluetoothTransport;

            _bluetoothConfiguration = new BluetoothConfiguration();
            ReadSettings();

            _deviceList = new List<BluetoothDeviceInfo>();
            _prevDeviceList = new List<BluetoothDeviceInfo>();
        }

        //Try to connect using current connections settings and trigger event if succesful
        protected override void DoWorkConnect()
        {
            const int timeOut = 1000;
            var activeConnection = false;

            try { activeConnection = TryConnection(timeOut); }
            catch { }

            if (activeConnection)
            {
                ConnectionFoundEvent();
            } 
        }

        // Perform scan to find connected systems
        protected override void DoWorkScan()
        {
            if (Thread.CurrentThread.Name == null) Thread.CurrentThread.Name = "BluetoothConnectionManager";
            var activeConnection = false;

            // Starting scan
            if (_scanType == ScanType.None) 
            {
                _scanType = ScanType.Quick;
            }
            if (_scanType == ScanType.Quick)
            {
                    
                try { activeConnection = QuickScan(); } catch { }
                _scanType = ScanType.Thorough;
            }
            else if (_scanType == ScanType.Thorough)
            {
                try { activeConnection = ThoroughScan(); } catch { }
                _scanType = ScanType.Quick;
            }

            // Trigger event when a connection was made
            if (activeConnection)
            {
                ConnectionFoundEvent();
            } 
        }

        private void QuickScanDevices()
        {
            // Fast
            _prevDeviceList = _deviceList;
            _deviceList.Clear();
            _deviceList.AddRange(_bluetoothTransport.BluetoothClient.DiscoverDevices(255, true, true, false, false));
        }


        private void ThorougScanForDevices()
        {
            // Slow
            _deviceList.Clear();
            _deviceList.AddRange(_bluetoothTransport.BluetoothClient.DiscoverDevices(65536, true, true, true, true));
        }

        private bool PairDevice(BluetoothDeviceInfo device)
        {
            if (device.Authenticated) return true;
            Log(2, "Trying to pair device " + device.DeviceName + " (" + device.DeviceAddress + ") ");
            
            // Check if PIN has been stored
            if (_bluetoothConfiguration.StoredDevicePins.ContainsKey(device.DeviceAddress))
            {
                Log(3, "Trying to stored key for device " + device.DeviceName );
                if (BluetoothSecurity.PairRequest(device.DeviceAddress, _bluetoothConfiguration.StoredDevicePins[device.DeviceAddress]))
                {
                    Log(2, "Pairing device " + device.DeviceName + " succesful! ");
                    return true;
                }
                // When trying PINS, you really need to wait in between
                Thread.Sleep(1000);   
            }            

            // loop through common PIN numbers to see if they pair
            foreach (string devicePin in CommonDevicePins)
            {
                // Trying to quickly can make your device lock
                
                Log(3, "Trying common pin " + devicePin + " for device " + device.DeviceName);
                var isPaired = BluetoothSecurity.PairRequest(device.DeviceAddress, devicePin);
                if (isPaired)
                {
                    _bluetoothConfiguration.StoredDevicePins[device.DeviceAddress] = devicePin;
                    StoreSettings();
                    Log(2, "Pairing device " + device.DeviceName + " succesful! ");
                    return true;
                }
                // When trying PINS, you really need to wait in between
                Thread.Sleep(1000);
            }

            Log(2, "Pairing device " + device.DeviceName + " unsuccesfull ");
            return true;
        }

        private bool TryConnection(BluetoothAddress bluetoothAddress, int timeOut)
        {
            if (bluetoothAddress == null) return false;
            // Find
            foreach (var bluetoothDeviceInfo in _deviceList)
            {
                if (bluetoothDeviceInfo.DeviceAddress == bluetoothAddress)
                {
                    return TryConnection(bluetoothDeviceInfo, timeOut);
                }
            }
            return false;
        }

        private bool TryConnection(BluetoothDeviceInfo bluetoothDeviceInfo, int timeOut)
        {
            // Try specific settings
            _bluetoothTransport.CurrentBluetoothDeviceInfo = bluetoothDeviceInfo;
            return TryConnection(timeOut); 
        }

        private bool TryConnection(int timeOut)
        {
            lock (_tryConnectionLock)
            {
                // Check if an (old) connection exists
                if (_bluetoothTransport.CurrentBluetoothDeviceInfo == null) return false;

                Connected = false;
                Log(1, @"Trying Bluetooth device " + _bluetoothTransport.CurrentBluetoothDeviceInfo.DeviceName);
                if (_bluetoothTransport.Connect())
                {
                    Log(3,
                        @"Connected with Bluetooth device " + _bluetoothTransport.CurrentBluetoothDeviceInfo.DeviceName +
                        ", requesting response");

                    DeviceStatus status = ArduinoAvailable(timeOut, 5);
                    Connected = (status == DeviceStatus.Available);

                    if (Connected)
                    {
                        Log(1,
                            "Connected with Bluetooth device " +
                            _bluetoothTransport.CurrentBluetoothDeviceInfo.DeviceName);
                        StoreSettings();
                    }
                    else
                    {
                        Log(3,
                            @"Connected with Bluetooth device " +
                            _bluetoothTransport.CurrentBluetoothDeviceInfo.DeviceName + ", received no response");
                    }
                    return Connected;
                }
                else
                {
                    Log(3,
                    @"No connection made with Bluetooth device " + _bluetoothTransport.CurrentBluetoothDeviceInfo.DeviceName );
                }
                return false;
            }
        }

        protected override void StartScan()
        {
            base.StartScan();

            if (ConnectionManagerState == ConnectionManagerState.Scan)
            {
                _scanType = ScanType.None;
            }
        }
    
        public bool QuickScan()
        {            
            Log(3, "Performing quick scan");
            const int longTimeOut =  1000;
            const int shortTimeOut = 1000;

            // First try if currentConnection is open or can be opened
            if (TryConnection(longTimeOut)) return true;

            // Do a quick rescan of all devices in range
            QuickScanDevices();

            if (PersistentSettings)
            {
                // Then try if last stored connection can be opened
                Log(3, "Trying last stored connection");
                if (TryConnection(_bluetoothConfiguration.BluetoothAddress, longTimeOut)) return true;
            }

            // Then see if new devices have been added to the list 
            if (NewDevicesScan()) return true;

            foreach (var device in _deviceList)
            {

                Thread.Sleep(100); // Bluetooth devices seem to work more reliably with some waits 
                Log(1, "Trying Device " + device.DeviceName + " (" + device.DeviceAddress + ") " );
                if (TryConnection(device, shortTimeOut)) return true;
            }

            return false;
        }

        public bool ThoroughScan()
        {
            Log(3, "Performing thorough scan");
            const int longTimeOut = 1000;
            const int shortTimeOut = 1000;

            // First try if currentConnection is open or can be opened
            if (TryConnection(longTimeOut)) return true;

            // Do a quick rescan of all devices in range
            ThorougScanForDevices();

            // Then try if last stored connection can be opened
            Log(3, "Trying last stored connection");
            if (TryConnection(_bluetoothConfiguration.BluetoothAddress,  longTimeOut)) return true;

            // Then see if new devices have been added to the list 
            if (NewDevicesScan()) return true;

            foreach (var device in _deviceList)
            {
                Thread.Sleep(100); // Bluetooth devices seem to work more reliably with some waits
                if (PairDevice(device))
                {
                    Log(1, "Trying Device " + device.DeviceName + " (" + device.DeviceAddress + ") ");
                    if (TryConnection(device, shortTimeOut)) return true;        
                }
            }
            return false;
        }

        private bool NewDevicesScan()
        {            
            const int shortTimeOut = 200;

            // Then see if port list has changed
            var newDevices = NewDevicesInList();
            if (newDevices.Count == 0) { return false; }
            
            Log(1, "Trying new devices");

            foreach (var device in newDevices)
            {
                if (TryConnection(device, shortTimeOut)) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private List<BluetoothDeviceInfo> NewDevicesInList()
        {
            return (from device in _deviceList from prevdevice in _prevDeviceList where device.DeviceAddress != prevdevice.DeviceAddress select device).ToList();
        }

        protected override void StoreSettings()
        {
            if (PersistentSettings)
            {
                _bluetoothConfiguration.BluetoothAddress = _bluetoothTransport.CurrentBluetoothDeviceInfo.DeviceAddress;

                var fileStream = File.Create(SettingsFileName);
                var serializer = new BinaryFormatter();
                serializer.Serialize(fileStream, _bluetoothConfiguration);
                fileStream.Close();
            }
        }

        protected override void ReadSettings()
        {
            // Read from file
            if (PersistentSettings && File.Exists(SettingsFileName))
            {
                var fileStream = File.OpenRead(SettingsFileName);
                var deserializer = new BinaryFormatter();
                _bluetoothConfiguration = (BluetoothConfiguration)deserializer.Deserialize(fileStream);
                fileStream.Close();
            }
        }
    }
}
