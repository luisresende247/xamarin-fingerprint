﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Foundation;
using LocalAuthentication;
using ObjCRuntime;
using Plugin.Fingerprint.Abstractions;
#if !__MAC__
using UIKit;
#endif

namespace Plugin.Fingerprint
{
    internal class FingerprintImplementation : FingerprintImplementationBase
    {
        private LAContext _context;

        public FingerprintImplementation()
        {
            CreateLaContext();
        }

        protected override async Task<FingerprintAuthenticationResult> NativeAuthenticateAsync(AuthenticationRequestConfiguration authRequestConfig, CancellationToken cancellationToken = new CancellationToken())
        {
            var result = new FingerprintAuthenticationResult();
            SetupContextProperties(authRequestConfig);

            Tuple<bool, NSError> resTuple;
            using (cancellationToken.Register(CancelAuthentication))
            {
                var policy = GetPolicy(authRequestConfig.AllowAlternativeAuthentication);
                resTuple = await _context.EvaluatePolicyAsync(policy, authRequestConfig.Reason);
            }

            if (resTuple.Item1)
            {
                result.Status = FingerprintAuthenticationResultStatus.Succeeded;
            }
            else
            {
                // #79 simulators return null for any reason
                if (resTuple.Item2 == null)
                {
                    result.Status = FingerprintAuthenticationResultStatus.UnknownError;
                    result.ErrorMessage = "";
                }
                else
                {
                    result = GetResultFromError(resTuple.Item2);
                }
            }

            CreateNewContext();
            return result;
        }

        public override async Task<FingerprintAvailability> GetAvailabilityAsync(bool allowAlternativeAuthentication = false, bool getAvailabilityType = false)
        {
            NSError error;

            if (_context == null)
                return FingerprintAvailability.NoApi;

            var policy = GetPolicy(allowAlternativeAuthentication);
            if (_context.CanEvaluatePolicy(policy, out error))
            {
                if (getAvailabilityType && UIDevice.CurrentDevice.CheckSystemVersion(11, 0))
                {
                    return _context.BiometryType == LABiometryType.TouchId ? FingerprintAvailability.AvailableTouchID : FingerprintAvailability.AvailableFaceID;
                }
                else
                {
                    return FingerprintAvailability.Available;
                }
            }

            switch ((LAStatus)(int)error.Code)
            {
                case LAStatus.TouchIDNotAvailable:
                    return FingerprintAvailability.NoSensor;
                case LAStatus.TouchIDNotEnrolled:
                case LAStatus.PasscodeNotSet:
                    return FingerprintAvailability.NoFingerprint;
                default:
                    return FingerprintAvailability.Unknown;
            }
        }

        private void SetupContextProperties(AuthenticationRequestConfiguration authRequestConfig)
        {
            if (_context.RespondsToSelector(new Selector("localizedFallbackTitle")))
            {
                _context.LocalizedFallbackTitle = authRequestConfig.FallbackTitle;
            }

            if (_context.RespondsToSelector(new Selector("localizedCancelTitle")))
            {
                _context.LocalizedCancelTitle = authRequestConfig.CancelTitle;
            }
        }

        private LAPolicy GetPolicy(bool allowAlternativeAuthentication)
        {
            return allowAlternativeAuthentication ?
                LAPolicy.DeviceOwnerAuthentication :
                LAPolicy.DeviceOwnerAuthenticationWithBiometrics;
        }

        private FingerprintAuthenticationResult GetResultFromError(NSError error)
        {
            var result = new FingerprintAuthenticationResult();

            switch ((LAStatus)(int)error.Code)
            {
                case LAStatus.AuthenticationFailed:
                    var description = error.Description;
                    if (description != null && description.Contains("retry limit exceeded"))
                    {
                        result.Status = FingerprintAuthenticationResultStatus.TooManyAttempts;
                    }
                    else
                    {
                        result.Status = FingerprintAuthenticationResultStatus.Failed;
                    }
                    break;

                case LAStatus.UserCancel:
                case LAStatus.AppCancel:
                    result.Status = FingerprintAuthenticationResultStatus.Canceled;
                    break;

                case LAStatus.UserFallback:
                    result.Status = FingerprintAuthenticationResultStatus.FallbackRequested;
                    break;

                case LAStatus.TouchIDLockout:
                    result.Status = FingerprintAuthenticationResultStatus.TooManyAttempts;
                    break;

                default:
                    result.Status = FingerprintAuthenticationResultStatus.UnknownError;
                    break;
            }

            result.ErrorMessage = error.LocalizedDescription;

            return result;
        }

        private void CancelAuthentication()
        {
            CreateNewContext();
        }

        private void CreateNewContext()
        {
            if (_context != null)
            {
                if (_context.RespondsToSelector(new Selector("invalidate")))
                {
                    _context.Invalidate();
                }
                _context.Dispose();
            }

            CreateLaContext();
        }

        private void CreateLaContext()
        {
            var info = new NSProcessInfo();
#if __MAC__
            var minVersion = new NSOperatingSystemVersion(10, 12, 0);
            if (!info.IsOperatingSystemAtLeastVersion(minVersion))
                return;
#else
			if (!UIDevice.CurrentDevice.CheckSystemVersion(8, 0))
				return;
#endif
            // Check LAContext is not available on iOS7 and below, so check LAContext after checking iOS version.
            if (Class.GetHandle(typeof(LAContext)) == IntPtr.Zero)
                return;

            _context = new LAContext();
        }
    }
}