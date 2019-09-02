﻿#if !__ANDROID__ && !__CONSTRAINED__ && !WINDOWS_RUNTIME && !UNITY_STANDALONE_LINUX
using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Lidgren.Network
{
	public static partial class NetUtility
	{
		private static readonly long s_timeInitialized = Stopwatch.GetTimestamp();
		private static readonly double s_dInvFreq = 1.0 / Stopwatch.Frequency;
        private static readonly SHA256 s_sha = SHA256.Create();

        [CLSCompliant(false)]
		public static ulong GetPlatformSeed(int seedInc)
		{
			ulong seed = (ulong)Stopwatch.GetTimestamp();
			return seed ^ ((ulong)Environment.WorkingSet + (ulong)seedInc);
		}

        public static double Now => (Stopwatch.GetTimestamp() - s_timeInitialized) * s_dInvFreq;

        private static NetworkInterface GetNetworkInterface()
		{
			var computerProperties = IPGlobalProperties.GetIPGlobalProperties();
			if (computerProperties == null)
				return null;

			var nics = NetworkInterface.GetAllNetworkInterfaces();
			if (nics == null || nics.Length < 1)
				return null;

			NetworkInterface best = null;
			foreach (NetworkInterface adapter in nics)
			{
				if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Unknown)
					continue;

				if (!adapter.Supports(NetworkInterfaceComponent.IPv4))
					continue;

				if (best == null)
					best = adapter;

				if (adapter.OperationalStatus != OperationalStatus.Up)
					continue;

				// make sure this adapter has any ipv4 addresses
				IPInterfaceProperties properties = adapter.GetIPProperties();
				foreach (UnicastIPAddressInformation unicastAddress in properties.UnicastAddresses)
				{
					if (unicastAddress != null && 
                        unicastAddress.Address != null &&
                        unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
					{
						// Yes it does, return this network interface.
						return adapter;
					}
				}
			}
			return best;
		}

		/// <summary>
		/// If available, returns the bytes of the physical (MAC) address for the first usable network interface.
		/// </summary>
		public static byte[] GetMacAddressBytes()
		{
			var ni = GetNetworkInterface();
			if (ni == null)
				return null;
			return ni.GetPhysicalAddress().GetAddressBytes();
		}

		public static IPAddress GetBroadcastAddress()
		{
			var ni = GetNetworkInterface();
			if (ni == null)
				return null;

			var properties = ni.GetIPProperties();
			foreach (UnicastIPAddressInformation unicastAddress in properties.UnicastAddresses)
			{
				if (unicastAddress != null && unicastAddress.Address != null && unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
				{
					var mask = unicastAddress.IPv4Mask;
					byte[] ipAdressBytes = unicastAddress.Address.GetAddressBytes();
					byte[] subnetMaskBytes = mask.GetAddressBytes();

					if (ipAdressBytes.Length != subnetMaskBytes.Length)
						throw new ArgumentException("Lengths of IP address and subnet mask do not match.");

					byte[] broadcastAddress = new byte[ipAdressBytes.Length];
					for (int i = 0; i < broadcastAddress.Length; i++)
						broadcastAddress[i] = (byte)(ipAdressBytes[i] | (subnetMaskBytes[i] ^ 255));

					return new IPAddress(broadcastAddress);
				}
			}
			return IPAddress.Broadcast;
		}

		/// <summary>
		/// Gets my local IPv4 address (not necessarily external) and subnet mask.
		/// </summary>
		public static IPAddress GetMyAddress(out IPAddress mask)
		{
			var ni = GetNetworkInterface();
			if (ni == null)
			{
				mask = null;
				return null;
			}

			IPInterfaceProperties properties = ni.GetIPProperties();
			foreach (UnicastIPAddressInformation unicastAddress in properties.UnicastAddresses)
			{
				if (unicastAddress != null &&
                    unicastAddress.Address != null &&
                    unicastAddress.Address.AddressFamily == AddressFamily.InterNetwork)
				{
					mask = unicastAddress.IPv4Mask;
					return unicastAddress.Address;
				}
			}

			mask = null;
			return null;
		}

		public static IPAddress CreateAddressFromBytes(byte[] bytes)
		{
			return new IPAddress(bytes);
		}
		
		public static byte[] ComputeSHAHash(byte[] bytes, int offset, int count)
		{
			return s_sha.ComputeHash(bytes, offset, count);
		}
	}
}
#endif