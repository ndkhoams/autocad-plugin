using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Win32;
using Autodesk.AutoCAD.EditorInput;

namespace CADtools
{
    // Quan ly ban quyen (offline, ky RSA). Plugin CHI giu PUBLIC key -> nguoi dung khong the tu tao key.
    public static class LicenseManager
    {
        // ===================== CAU HINH =====================
        // Dan PUBLIC key (base64 cua RSA public key XML) sinh boi cong cu KeyGen (lenh: keygen init).
        private const string PublicKeyB64 = "PFJTQUtleVZhbHVlPjxNb2R1bHVzPnZVUDJGNXpGRER1NjVyU1NuSENNMmZ4WStVaDJQaGtwakoxWTJpYUpvTHQ1K2dHYmY0RUJuTHRuMmlucTFNSXJIRnIxZUg0MXgrektLWnZObytWazhHQ2VrVHRCcXBTT3dSWmsrTnNDdHJ6QUEzaklZdFRnVU4vN1NvMDdxcHp3eFBqb3hWM0gzUE9lK3ByMUhVdVJJMjYzWTUxb044U1NxU05OaHVrSUlLaTRIZzd2b2EzL1hUaDJab0RudlNFYlEzVUVLcGxpTkdkbHcwMzdhbTFENnV6eURuM3oxMm1sNEVnZml4UmRGMUYrRmFha3ZQUWNaQmlIZjg3NTZUNklic2JlVG14aHlsVjA3N2dpTGxaVklIQWEvdnU2SklmSlFIYVR6M1pmNmNoMHVtZk54b2dSMll0WFlGdUViNlllYjRGZkJqaW9KMWk1WTVacDVhMFN2UT09PC9Nb2R1bHVzPjxFeHBvbmVudD5BUUFCPC9FeHBvbmVudD48L1JTQUtleVZhbHVlPg==";

        private const string Product = "CADtools";
        // Cho phep lech bao nhieu ngay truoc khi coi la chinh nguoc dong ho.
        private const int ClockRollbackGraceDays = 2;
        // MAC DINH bat buoc key phai khoa theo may. Doi thanh false neu muon cho phep key chay moi may.
        private const bool RequireMachineLock = true;
        // ====================================================

        private static bool _checked;
        private static bool _ok;
        private static string _status = "Chưa kiểm tra";
        private static DateTime _expiry = DateTime.MinValue;
        private static string _licName = "";

        public static bool IsValid { get { EnsureChecked(); return _ok; } }
        public static string Status { get { EnsureChecked(); return _status; } }
        public static DateTime Expiry { get { EnsureChecked(); return _expiry; } }
        public static string LicenseName { get { EnsureChecked(); return _licName; } }

        // Goi o DAU moi command. Neu khong hop le -> in thong bao va tra false.
        public static bool Ensure(Editor ed)
        {
            EnsureChecked();
            if (!_ok && ed != null)
            {
                ed.WriteMessage("\n[" + Product + "] Bản quyền không hợp lệ: " + _status
                    + "\nDùng lệnh CDLIC để nhập key, hoặc CDMAY để lấy mã máy.\n");
            }
            return _ok;
        }

        public static void Recheck() { _checked = false; EnsureChecked(); }

        private static void EnsureChecked()
        {
            if (_checked) return;
            _checked = true;
            try
            {
                string lic = LoadLicenseString();
                if (string.IsNullOrEmpty(lic)) { _ok = false; _status = "Chưa có key."; return; }

                LicensePayload p;
                if (!VerifyAndParse(lic, out p, out _status)) { _ok = false; return; }

                _licName = p.Name ?? "";
                _expiry = p.Expiry;

                // Chong chinh nguoc dong ho he thong.
                DateTime now = DateTime.Now.Date;
                DateTime lastSeen = ReadLastSeen();
                if (lastSeen != DateTime.MinValue && now < lastSeen.AddDays(-ClockRollbackGraceDays))
                {
                    _ok = false;
                    _status = "Phát hiện đồng hồ hệ thống bị chỉnh ngược.";
                    return;
                }
                WriteLastSeen(now > lastSeen ? now : lastSeen);

                if (now > p.Expiry.Date)
                {
                    _ok = false;
                    _status = "Key đã hết hạn ngày " + p.Expiry.ToString("yyyy-MM-dd") + ".";
                    return;
                }

                // Khoa theo may. MAC DINH bat buoc (RequireMachineLock).
                if (string.IsNullOrEmpty(p.Machine))
                {
                    if (RequireMachineLock)
                    {
                        _ok = false;
                        _status = "Key này không khóa theo máy (không được phép).";
                        return;
                    }
                }
                else
                {
                    string fp = GetMachineFingerprint();
                    if (!string.Equals(fp, p.Machine, StringComparison.OrdinalIgnoreCase))
                    {
                        _ok = false;
                        _status = "Key không dành cho máy này.";
                        return;
                    }
                }

                _ok = true;
                int days = (p.Expiry.Date - now).Days;
                _status = "Hợp lệ. Cấp cho: " + _licName + ". Còn " + days
                    + " ngày (hết hạn " + p.Expiry.ToString("yyyy-MM-dd") + ").";
            }
            catch (Exception ex)
            {
                _ok = false;
                _status = "Lỗi kiểm tra key: " + ex.Message;
            }
        }

        // ===================== Xac thuc =====================
        private struct LicensePayload
        {
            public string Name;
            public DateTime Expiry;
            public string Machine;
        }

        private static bool VerifyAndParse(string lic, out LicensePayload payload, out string err)
        {
            payload = new LicensePayload();
            err = "";
            try
            {
                int dot = lic.IndexOf('.');
                if (dot <= 0) { err = "Định dạng key sai."; return false; }
                byte[] data = FromB64Url(lic.Substring(0, dot));
                byte[] sig = FromB64Url(lic.Substring(dot + 1));

                using (var rsa = new RSACryptoServiceProvider())
                {
                    rsa.PersistKeyInCsp = false;
                    string xml = Encoding.UTF8.GetString(Convert.FromBase64String(PublicKeyB64));
                    rsa.FromXmlString(xml);
                    if (!rsa.VerifyData(data, "SHA256", sig))
                    {
                        err = "Chữ ký không hợp lệ (key giả hoặc bị sửa).";
                        return false;
                    }
                }

                string json = Encoding.UTF8.GetString(data);
                payload.Name = JsonGet(json, "name");
                payload.Machine = JsonGet(json, "machine");
                string exp = JsonGet(json, "expiry");
                DateTime dt;
                if (!DateTime.TryParse(exp, out dt)) { err = "Ngày hết hạn sai."; return false; }
                payload.Expiry = dt;
                return true;
            }
            catch (Exception ex) { err = "Lỗi đọc key: " + ex.Message; return false; }
        }

        // Ma may on dinh: SHA256(MachineGuid + ten may), lay 16 ky tu hex.
        public static string GetMachineFingerprint()
        {
            string guid = "";
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                {
                    if (k != null) guid = (k.GetValue("MachineGuid") as string) ?? "";
                }
            }
            catch { }
            string raw = (guid + "|" + Environment.MachineName).ToUpperInvariant();
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(h[i].ToString("X2"));
                return sb.ToString();
            }
        }

        // ===================== Luu / doc license =====================
        private static string LicPathAppData()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Product);
            return Path.Combine(dir, "license.lic");
        }

        private static string LoadLicenseString()
        {
            // 1) File canh DLL: CADtools.lic
            try
            {
                string dll = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string p = Path.Combine(Path.GetDirectoryName(dll), Product + ".lic");
                if (File.Exists(p)) return File.ReadAllText(p).Trim();
            }
            catch { }
            // 2) %APPDATA%\CADtools\license.lic
            try { string p = LicPathAppData(); if (File.Exists(p)) return File.ReadAllText(p).Trim(); } catch { }
            // 3) Registry HKCU\Software\CADtools\License
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + Product))
                    if (k != null)
                    {
                        var v = k.GetValue("License") as string;
                        if (!string.IsNullOrEmpty(v)) return v.Trim();
                    }
            }
            catch { }
            return "";
        }

        public static bool InstallLicense(string lic, out string msg)
        {
            lic = (lic ?? "").Trim();
            LicensePayload p; string err;
            if (!VerifyAndParse(lic, out p, out err)) { msg = "Key không hợp lệ: " + err; return false; }
            try
            {
                string p2 = LicPathAppData();
                Directory.CreateDirectory(Path.GetDirectoryName(p2));
                File.WriteAllText(p2, lic);
            }
            catch (Exception ex) { msg = "Không ghi được file license: " + ex.Message; return false; }
            try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + Product)) k.SetValue("License", lic); } catch { }
            Recheck();
            msg = "Đã cài key. " + Status;
            return _ok;
        }

        private static DateTime ReadLastSeen()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\" + Product))
                    if (k != null)
                    {
                        var v = k.GetValue("LastSeen") as string;
                        DateTime dt;
                        if (!string.IsNullOrEmpty(v) && DateTime.TryParse(v, out dt)) return dt.Date;
                    }
            }
            catch { }
            return DateTime.MinValue;
        }

        private static void WriteLastSeen(DateTime d)
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(@"Software\" + Product)) k.SetValue("LastSeen", d.ToString("yyyy-MM-dd")); }
            catch { }
        }

        // ===================== Helpers =====================
        private static byte[] FromB64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
            return Convert.FromBase64String(s);
        }

        private static string JsonGet(string json, string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(json,
                "\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}