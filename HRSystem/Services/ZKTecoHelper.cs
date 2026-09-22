using System;
using System.Text;
using System.Globalization;

namespace HRSystem.Services
{
    public static class ZKTecoHelper
    {
        public static string EncodeForZKTeco(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                // معظم أجهزة البصمة في الشرق الأوسط تستخدم ترميز Windows-1256 للغة العربية
                var arabicEncoding = Encoding.GetEncoding(1256);
                byte[] arabicBytes = arabicEncoding.GetBytes(input);
                
                // الحصول على ترميز ANSI الفعلي لنظام التشغيل (مثل Windows-1252 في الأنظمة الإنجليزية أو Windows-1256 في الأنظمة العربية)
                int ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (ansiCodePage == 0 || ansiCodePage == 65001) // في حال كان الافتراضي UTF-8 أو غير محدد
                {
                    ansiCodePage = 1252; // الترميز الغربي الافتراضي
                }
                
                var systemAnsiEncoding = Encoding.GetEncoding(ansiCodePage);
                
                // نقوم بإعادة بناء النص باستخدام ترميز ANSI الخاص بالنظام
                // حتى عندما يقوم الـ COM Interop بتحويله مرة أخرى، يحافظ على بايتات اللغة العربية الأصلية
                return systemAnsiEncoding.GetString(arabicBytes);
            }
            catch
            {
                return input; // Fallback
            }
        }

        public static string DecodeFromZKTeco(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                
                // الحصول على ترميز ANSI الفعلي لنظام التشغيل الذي استخدمه الـ COM Interop لقراءة النص
                int ansiCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
                if (ansiCodePage == 0 || ansiCodePage == 65001) // في حال كان الافتراضي UTF-8 أو غير محدد
                {
                    ansiCodePage = 1252; // الترميز الغربي الافتراضي
                }
                
                var systemAnsiEncoding = Encoding.GetEncoding(ansiCodePage);
                
                // تحويل النص (الذي تم تفسيره بشكل خاطئ كـ ANSI) إلى بايتاته الأصلية
                byte[] rawBytes = systemAnsiEncoding.GetBytes(input);
                
                // ترميز Windows-1256 للغة العربية لفك التشفير الصحيح للبايتات
                var arabicEncoding = Encoding.GetEncoding(1256);
                return arabicEncoding.GetString(rawBytes);
            }
            catch
            {
                return input; // Fallback
            }
        }

        public static bool IsGarbled(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            
            // إذا كان الاسم يحتوي بالفعل على حروف عربية، فهو ليس تالفاً (مشفراً بالخطأ)
            if (HasArabic(input)) return false;
            
            // نقوم بمحاولة فك التشفير
            string decoded = DecodeFromZKTeco(input);
            
            // إذا كان النص بعد فك التشفير يحتوي على حروف عربية، إذن النص الأصلي كان تالفاً (مشفراً بالخطأ)
            return HasArabic(decoded);
        }

        public static bool HasArabic(string input)
        {
            if (string.IsNullOrEmpty(input)) return false;
            foreach (char c in input)
            {
                // نطاق الحروف العربية في نظام الترميز الموحد Unicode
                if (c >= 0x0600 && c <= 0x06FF)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
