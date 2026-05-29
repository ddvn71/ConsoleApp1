using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using Tesseract;

namespace NinjaSchoolConsoleTool
{
    class Program
    {
        // Nhập các hàm Win32 API để tìm cửa sổ con Render ẩn và gửi thông điệp chạy ẩn
        [DllImport("user32.dll", EntryPoint = "PostMessageA", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;

        // Mã phím Virtual-Key của bàn phím PC
        private const byte VK_LEFT = 0x25;
        private const byte VK_UP = 0x26;
        private const byte VK_RIGHT = 0x27;
        private const byte VK_DOWN = 0x28;

        private static string adbPath = "";
        private static string ldPlayerPath = "";
        private static string deviceId = "";
        private static readonly string tessdataPath = @"./tessdata";
        private static readonly string configFile = "config.txt";
        private static readonly string imageFolder = "templates-img";
        private static CancellationTokenSource cts;

        // Bộ nhớ tạm thời kiểm tra trạng thái đào rương thành công
        private static bool isTreasureFound = false;

        // Hướng đi quét được từ OCR: 0=Random, 1=Trái, 2=Trái Dưới, 3=Trên, 4=Phải, 5=Phải Dưới, 6=Phía Dưới, 7=Trái Trên, 8=Phải Trên
        private static int nextDirection = 0;

        // ĐỐI TƯỢNG KHÓA LUỒNG - ĐẢM BẢO AN TOÀN TRÊN .NET 10
        private static readonly object _consoleLock = new object();

        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "NINJA SCHOOL ONLINE - AUTO TREASURE SYSTEM";

            ShowHeader();

            // 1. Kiểm tra tài nguyên hệ thống (Chỉ hiển thị cảnh báo nếu thiếu)
            CheckResources();

            // 2. Khởi tạo cấu hình hệ thống
            LoadConfiguration();

            // 3. Kiểm tra kết nối ADB với giả lập
            if (!ConfigureAdbPath())
            {
                WriteLog("LỖI: Sửa đường dẫn trong 'config.txt' rồi mở lại Tool.", ConsoleColor.Red);
                Console.ReadLine();
                return;
            }

            // 4. KHỞI CHẠY AUTO TRỰC TIẾP NGAY LẬP TỨC
            StartLdPlayer();
            cts = new CancellationTokenSource();

            WriteLog("------------------------------------------------------------------", ConsoleColor.DarkGray);
            WriteLog(" 🚀 HỆ THỐNG AUTO TÌM KHO BÁU ĐÃ ĐƯỢC KÍCH HOẠT (CHẠY ẨN HOÀN TOÀN)", ConsoleColor.Cyan);
            WriteLog(" Hướng dẫn: Nhấn phím 'Q' và ấn Enter bất kỳ lúc nào để DỪNG & THOÁT.", ConsoleColor.Yellow);
            WriteLog("------------------------------------------------------------------", ConsoleColor.DarkGray);

            Task autoTask = Task.Run(() => StartAutoLoop(cts.Token), cts.Token);

            // Chờ lệnh dừng từ phím Q của người dùng
            while (true)
            {
                string input = Console.ReadLine()?.Trim().ToLower();
                if (input == "q")
                {
                    cts.Cancel();
                    ClearStatusLines(); // Dọn dẹp dòng ghi đè
                    WriteLog("🔄 Đang tắt chương trình Auto, vui lòng chờ giây lát...", ConsoleColor.Yellow);
                    break;
                }
            }

            try
            {
                autoTask.Wait();
            }
            catch { }

            WriteLog("Đã tắt Auto hoàn toàn. Nhấn phím bất kỳ để thoát...", ConsoleColor.Green);
            Console.ReadKey();
        }

        #region Kiểm tra tài nguyên hình ảnh mẫu

        private static void CheckResources()
        {
            WriteLog("🔍 Đang kiểm tra hệ thống tài nguyên...", ConsoleColor.Cyan);

            if (!Directory.Exists(tessdataPath))
            {
                WriteLog($"⚠️ Cảnh báo thiếu thư mục: '{tessdataPath}' (Cần bổ sung nếu sử dụng OCR)", ConsoleColor.Yellow);
            }
            else if (!File.Exists(Path.Combine(tessdataPath, "vie.traineddata")))
            {
                WriteLog($"⚠️ Cảnh báo thiếu file ngôn ngữ: '{tessdataPath}/vie.traineddata' (Cần bổ sung nếu sử dụng OCR)", ConsoleColor.Yellow);
            }

            if (!Directory.Exists(imageFolder))
            {
                Directory.CreateDirectory(imageFolder);
                WriteLog($"📁 Đã tự động khởi tạo thư mục lưu ảnh: '{imageFolder}'", ConsoleColor.Green);
            }

            // Danh sách ảnh mẫu phục vụ chu trình cốt lõi
            string[] requiredImages = { "hanh_trang.png", "su_dung.png", "su_dung2.png", "xeng_dao.png", "ok_final.png", "dung_tui.png", "dong_dong.png" };
            foreach (string img in requiredImages)
            {
                string path = Path.Combine(imageFolder, img);
                if (!File.Exists(path))
                {
                    WriteLog($"⚠️ Cảnh báo thiếu file ảnh mẫu: '{imageFolder}/{img}'", ConsoleColor.Yellow);
                }
            }
        }

        #endregion

        #region Vòng Lặp Auto Chính (Main Flow)

        private static async Task StartAutoLoop(CancellationToken token)
        {
            string screenPath = "temp_screen.png";

            // Đường dẫn hình ảnh mẫu
            string bagTemplate = Path.Combine(imageFolder, "hanh_trang.png");
            string shovelTemplate = Path.Combine(imageFolder, "xeng_dao.png");
            string useTemplate1 = Path.Combine(imageFolder, "su_dung.png");
            string useTemplate2 = Path.Combine(imageFolder, "su_dung2.png");
            string okTemplate = Path.Combine(imageFolder, "ok_final.png");
            string stopScrollTemplate = Path.Combine(imageFolder, "dung_tui.png");
            string closeTemplate = Path.Combine(imageFolder, "dong_dong.png");

            RunAdbCommand("start-server");

            while (!token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();

                // Ghi đè trạng thái hệ thống bằng 2 dòng thông tin
                WriteStatus("Đang chụp màn hình giả lập...", "Chuẩn bị chạy vòng lặp chính.");
                CaptureScreen(screenPath);

                if (!File.Exists(screenPath))
                {
                    WriteStatus("Đang chờ kết nối thiết bị...", "Vui lòng mở giả lập LDPlayer");
                    await Task.Delay(2000, token);
                    continue;
                }

                ClearStatusLines();
                WriteLog("🎬 [Vòng Lặp Chính] Bắt đầu chu trình quét mới...", ConsoleColor.Magenta);

                // ==========================================
                // BƯỚC 1: SỬ DỤNG XẺNG ĐÀO (Sử dụng 1 -> Sử dụng 2)
                // ==========================================
                bool isShovelUsed = await OpenBagAndUseItem(token, screenPath, bagTemplate, shovelTemplate, null, useTemplate1, useTemplate2, stopScrollTemplate, "Xẻng đào");
                if (!isShovelUsed)
                {
                    WriteLog("❌ Lỗi: Tiến trình sử dụng Xẻng đào thất bại! Thử lại sau 3 giây...", ConsoleColor.Red);
                    await Task.Delay(3000, token);
                    continue;
                }

                // KIỂM TRA ĐÀO THÀNH CÔNG: Nếu sau 10 giây không thấy nút ok_final.png hiện lên -> Chúc mừng!
                if (isTreasureFound)
                {
                    WriteLog("🎉 [KẾT QUẢ] ĐÀO KHO BÁU THÀNH CÔNG! Đang dọn dẹp các bảng thông báo chúc mừng...", ConsoleColor.Green);

                    // 1. Tìm và click đóng nút ok_final.png trước tiên
                    CaptureScreen(screenPath);
                    string okFinalTemplate = Path.Combine(imageFolder, "ok_final.png");
                    System.Drawing.Point? okFinalLocation = FindTemplate(screenPath, okFinalTemplate);
                    if (okFinalLocation != null)
                    {
                        WriteLog($"✅ Bấm đóng nút thành công [ok_final.png] tại: X={okFinalLocation.Value.X}, Y={okFinalLocation.Value.Y}", ConsoleColor.Green);
                        Tap(okFinalLocation.Value.X, okFinalLocation.Value.Y);
                        await Task.Delay(500, token); // Đợi bảng 1 đóng hoàn toàn
                    }

                    // 2. Chụp quét và click tiếp nút Đóng dong_dong.png ngay sau đó
                    CaptureScreen(screenPath);
                    System.Drawing.Point? closeLoc = FindTemplate(screenPath, closeTemplate);
                    if (closeLoc != null)
                    {
                        WriteLog($"✅ Bấm đóng bảng phụ rương đồ [dong_dong.png] tại: X={closeLoc.Value.X}, Y={closeLoc.Value.Y}", ConsoleColor.Green);
                        Tap(closeLoc.Value.X, closeLoc.Value.Y);
                        await Task.Delay(500, token); // Đợi bảng 2 đóng hoàn toàn
                    }

                    WriteLog("⏸️ Hệ thống dừng chờ đúng 15.0 giây để bạn nhặt rương đồ trước khi sang chu kỳ mới...", ConsoleColor.Green);
                    await Task.Delay(15000, token); // Treo máy chờ nhặt đồ 15 giây
                    isTreasureFound = false; // Reset trạng thái
                    continue; // Quay lại từ đầu chu trình mới
                }

                // ==========================================
                // BƯỚC 2: THỰC THI DI CHUYỂN PHỦ BẢN ĐỒ HÌNH VUÔNG X2 LẦN THEO HƯỚNG QUÉT ĐƯỢC TỪ OCR
                // ==========================================
                if (nextDirection == 1) // HƯỚNG BÊN TRÁI
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển phủ map bên TRÁI x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Chạy sang TRÁI 1.0 giây...", ConsoleColor.Blue);
                        SendBackgroundKey(VK_LEFT, 1000);
                        await Task.Delay(200, token);

                        WriteLog($"   => [Lần {i + 1}/2] Nhảy chéo LÊN + TRÁI 1.0 giây...", ConsoleColor.Blue);
                        HoldKeysTogether(VK_UP, VK_LEFT, 1000);
                        await Task.Delay(200, token);

                        WriteLog($"   => [Lần {i + 1}/2] Nhảy LÊN bám dây 1.0 giây...", ConsoleColor.Blue);
                        SendBackgroundKey(VK_UP, 1000);
                        await Task.Delay(200, token);

                        HoldKeysTogether(VK_UP, VK_RIGHT, 500); // Tránh kẹt
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 2) // HƯỚNG TRÁI PHÍA DƯỚI
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển bên TRÁI PHÍA DƯỚI x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Nhấn giữ XUỐNG + TRÁI cùng lúc 2.0 giây...", ConsoleColor.Blue);
                        HoldKeysTogether(VK_DOWN, VK_LEFT, 2000);
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 3) // HƯỚNG PHÍA TRÊN
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ nhảy LÊN thẳng đứng x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Nhấn giữ phím nhảy LÊN thẳng đứng 2.0 giây...", ConsoleColor.Blue);
                        SendBackgroundKey(VK_UP, 2000);
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 4) // HƯỚNG BÊN PHẢI
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển phủ map bên PHẢI x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Chạy sang PHẢI 1.0 giây...", ConsoleColor.Blue);
                        SendBackgroundKey(VK_RIGHT, 1000);
                        await Task.Delay(200, token);

                        WriteLog($"   => [Lần {i + 1}/2] Nhảy chéo LÊN + PHẢI 1.0 giây...", ConsoleColor.Blue);
                        HoldKeysTogether(VK_UP, VK_RIGHT, 1000);
                        await Task.Delay(200, token);

                        WriteLog($"   => [Lần {i + 1}/2] Nhảy LÊN bám dây 1.0 giây...", ConsoleColor.Blue);
                        SendBackgroundKey(VK_UP, 1000);
                        await Task.Delay(200, token);

                        HoldKeysTogether(VK_UP, VK_LEFT, 500); // Tránh kẹt
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 5) // HƯỚNG PHẢI PHÍA DƯỚI
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển bên PHẢI PHÍA DƯỚI x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Nhấn giữ XUỐNG + PHẢI cùng lúc 2.0 giây...", ConsoleColor.Blue);
                        HoldKeysTogether(VK_DOWN, VK_RIGHT, 2000);
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 6) // HƯỚNG PHÍA DƯỚI (Mới bổ sung theo yêu cầu)
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển PHÍA DƯỚI x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Nhấn giữ phím Mũi tên XUỐNG 2.0 giây...", ConsoleColor.Blue);
                        SendBackgroundKey(VK_DOWN, 2000);
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 7) // HƯỚNG TRÁI PHÍA TRÊN (Mới rẽ nhánh)
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển chéo TRÁI PHÍA TRÊN x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Nhấn giữ phím nhảy LÊN + TRÁI 2.0 giây...", ConsoleColor.Blue);
                        HoldKeysTogether(VK_UP, VK_LEFT, 2000);
                        await Task.Delay(500, token);
                    }
                }
                else if (nextDirection == 8) // HƯỚNG PHẢI PHÍA TRÊN (Mới rẽ nhánh)
                {
                    WriteLog("🏃 [Di Chuyển] Nhấn giữ di chuyển chéo PHẢI PHÍA TRÊN x2 lần...", ConsoleColor.Blue);
                    for (int i = 0; i < 2; i++)
                    {
                        WriteLog($"   => [Lần {i + 1}/2] Nhấn giữ phím nhảy LÊN + PHẢI 2.0 giây...", ConsoleColor.Blue);
                        HoldKeysTogether(VK_UP, VK_RIGHT, 2000);
                        await Task.Delay(500, token);
                    }
                }
                else // KHÔNG KHỚP HƯỚNG NÀO -> DI CHUYỂN RANDOM NGẪU NHIÊN CỨU KẸT
                {
                    WriteLog("🎲 Kích hoạt Di chuyển NGẪU NHIÊN để dò đường...", ConsoleColor.Yellow);
                    Random rand = new Random();
                    int randomAction = rand.Next(0, 3);

                    for (int i = 0; i < 2; i++)
                    {
                        if (randomAction == 0)
                        {
                            WriteLog($"🎲 [Ngẫu nhiên - Lần {i + 1}/2] Nhảy tránh kẹt sang TRÁI...", ConsoleColor.DarkGray);
                            SendBackgroundKey(VK_LEFT, 1000);
                            await Task.Delay(200, token);
                            HoldKeysTogether(VK_UP, VK_LEFT, 1000);
                            await Task.Delay(200, token);
                            SendBackgroundKey(VK_UP, 1000);
                            await Task.Delay(200, token);
                            HoldKeysTogether(VK_UP, VK_RIGHT, 500);
                        }
                        else if (randomAction == 1)
                        {
                            WriteLog($"🎲 [Ngẫu nhiên - Lần {i + 1}/2] Nhảy tránh kẹt sang PHẢI...", ConsoleColor.DarkGray);
                            SendBackgroundKey(VK_RIGHT, 1000);
                            await Task.Delay(200, token);
                            HoldKeysTogether(VK_UP, VK_RIGHT, 1000);
                            await Task.Delay(200, token);
                            SendBackgroundKey(VK_UP, 1000);
                            await Task.Delay(200, token);
                            HoldKeysTogether(VK_UP, VK_LEFT, 500);
                        }
                        else
                        {
                            WriteLog($"🎲 [Ngẫu nhiên - Lần {i + 1}/2] Nhảy thẳng đứng LÊN bậc thềm...", ConsoleColor.DarkGray);
                            SendBackgroundKey(VK_UP, 2000);
                            await Task.Delay(500, token);
                        }
                        await Task.Delay(500, token);
                    }
                }

                // Khoảng nghỉ cuối vòng lặp đúng 3 giây
                WriteLog("🔄 Hoàn thành vòng lặp. Nghỉ 3.0 giây chuẩn bị lặp lại...", ConsoleColor.DarkGray);
                await Task.Delay(3000, token);
            }
        }

        // HÀM HỖ TRỢ: Thực hiện mở hành trang -> Cuộn tìm Item -> Click sử dụng 1 -> Click sử dụng 2 -> Quét hướng đi -> Tắt bảng liên tiếp
        private static async Task<bool> OpenBagAndUseItem(CancellationToken token, string screenPath, string bagTemplate, string itemTemplate1, string itemTemplate2, string useTemplate1, string useTemplate2, string stopScrollTemplate, string itemName)
        {
            // TRƯỚC KHI MỞ HÀNH TRANG: Check dọn dẹp quảng cáo hoặc bảng lỗi dong_dong.png trước tiên
            string closeTemplateLocal = Path.Combine(imageFolder, "dong_dong.png");
            CheckAndDismissCloseButton(screenPath, closeTemplateLocal);

            CaptureScreen(screenPath);

            // Kiểm tra xem vật phẩm đã hiển thị sẵn trên màn hình chưa (hành trang đã mở sẵn từ bước trước)
            System.Drawing.Point? itemLocation = FindTemplate(screenPath, itemTemplate1);
            if (itemLocation == null && !string.IsNullOrEmpty(itemTemplate2))
            {
                itemLocation = FindTemplate(screenPath, itemTemplate2);
            }

            bool isBagAlreadyOpen = itemLocation != null;

            if (!isBagAlreadyOpen)
            {
                // Nếu chưa mở hành trang, tìm nút để click mở
                System.Drawing.Point? bagLocation = FindTemplate(screenPath, bagTemplate);

                // TỰ GIẢI CỨU KHI KẸT KHI KHÔNG TÌM THẤY NÚT HÀNH TRANG
                if (bagLocation == null)
                {
                    WriteLog("⚠️ Cảnh báo: Không tìm thấy nút Hành trang! Bắt đầu tiến hành di chuyển ngẫu nhiên tự giải vây...", ConsoleColor.Yellow);

                    Random rand = new Random();
                    while (bagLocation == null && !token.IsCancellationRequested)
                    {
                        int randomDir = rand.Next(0, 3);
                        switch (randomDir)
                        {
                            case 0:
                                WriteLog("🎲 [Giải vây] Nhấn giữ phím mũi tên TRÁI 1.0 giây...", ConsoleColor.DarkGray);
                                SendBackgroundKey(VK_LEFT, 1000);
                                break;
                            case 1:
                                WriteLog("🎲 [Giải vây] Nhấn giữ phím mũi tên PHẢI 1.0 giây...", ConsoleColor.DarkGray);
                                SendBackgroundKey(VK_RIGHT, 1000);
                                break;
                            case 2:
                                WriteLog("🎲 [Giải vây] Nhấn giữ phím nhảy LÊN 1.0 giây...", ConsoleColor.DarkGray);
                                SendBackgroundKey(VK_UP, 1000);
                                break;
                        }

                        await Task.Delay(1000, token);

                        CaptureScreen(screenPath);
                        bagLocation = FindTemplate(screenPath, bagTemplate);
                    }

                    WriteLog("✅ Đã khôi phục thành công! Phát hiện thấy nút Hành trang. Tiếp tục Flow...", ConsoleColor.Green);
                }

                WriteLog($"🎒 Đã tìm thấy nút Hành trang tại: X={bagLocation.Value.X}, Y={bagLocation.Value.Y}. Đang mở rương...", ConsoleColor.DarkGray);
                Tap(bagLocation.Value.X, bagLocation.Value.Y);
                await Task.Delay(800, token); // Đợi hòm đồ mở ra
            }
            else
            {
                WriteLog($"🎒 [Tự động phục hồi] Hành trang đã mở sẵn. Tiến hành xử lý [{itemName}]...", ConsoleColor.Green);
            }

            // 2. Cuộn tìm kiếm vật phẩm
            int scrollTimes = 1;
            bool reachedEnd = false;

            while (!reachedEnd)
            {
                CaptureScreen(screenPath);

                // Quét tìm vật phẩm mẫu 1
                itemLocation = FindTemplate(screenPath, itemTemplate1);
                bool isTemplate1 = itemLocation != null;

                // Nếu có vật phẩm mẫu 2 dự phòng và chưa tìm thấy mẫu 1, quét tìm tiếp mẫu 2
                if (itemLocation == null && !string.IsNullOrEmpty(itemTemplate2))
                {
                    itemLocation = FindTemplate(screenPath, itemTemplate2);
                }

                if (itemLocation != null)
                {
                    string finalName = isTemplate1 ? itemName + " 1" : itemName + " 2 (Dự phòng)";
                    WriteLog($"🎯 Đã quét tìm thấy [{finalName}] trong hành trang!", ConsoleColor.Green);
                    break;
                }

                // Kiểm tra xem đã cuộn chạm mốc dừng túi chưa
                System.Drawing.Point? stopLocation = FindTemplate(screenPath, stopScrollTemplate);
                if (stopLocation != null)
                {
                    WriteLog("🛑 Chạm mốc ranh giới dừng túi (dung_tui.png). Ngừng cuộn đồ!", ConsoleColor.Yellow);
                    reachedEnd = true;
                    break;
                }

                if (scrollTimes > 25) break;

                WriteLog($"👇 Chưa thấy [{itemName}], đang cuộn rương xuống dưới bằng phím XUỐNG ẩn (Lần thứ {scrollTimes})...", ConsoleColor.Yellow);
                ScrollInventoryDown();
                await Task.Delay(600, token);
                scrollTimes++;
            }

            if (itemLocation == null) return false;

            // Click vào vật phẩm vừa quét thấy
            Tap(itemLocation.Value.X, itemLocation.Value.Y);
            await Task.Delay(500, token);

            // 3. Click Sử dụng 1 (su_dung.png)
            CaptureScreen(screenPath);
            System.Drawing.Point? useLocation = FindTemplate(screenPath, useTemplate1);
            if (useLocation == null) return false;

            WriteLog($"⚡ Click 'Sử dụng 1' tại: X={useLocation.Value.X}, Y={useLocation.Value.Y}...", ConsoleColor.DarkGray);
            Tap(useLocation.Value.X, useLocation.Value.Y);
            await Task.Delay(800, token);

            // 4. Click Sử dụng 2 (su_dung2.png)
            CaptureScreen(screenPath);
            System.Drawing.Point? use2Location = FindTemplate(screenPath, useTemplate2);
            if (use2Location == null) return false;

            WriteLog($"⚡ Click 'Sử dụng 2' tại: X={use2Location.Value.X}, Y={use2Location.Value.Y}...", ConsoleColor.DarkGray);
            Tap(use2Location.Value.X, use2Location.Value.Y);
            await Task.Delay(1000, token); // Đợi bảng thông báo hiện lên

            // Check tiếp nút ok_final.png hoặc dong_dong.png phụ xuất hiện trong quá trình mở Xẻng
            if (itemName == "Xẻng đào")
            {
                string okFinalTemplate = Path.Combine(imageFolder, "ok_final.png");
                string closeTemplate = Path.Combine(imageFolder, "dong_dong.png");

                bool okFinalDetected = false;
                nextDirection = 0; // Reset hướng đi mặc định

                // TỐI ƯU MỚI: Tăng thời gian chờ quét nút lên 10.0 giây (20 lần quét, mỗi lần cách nhau 500ms)
                WriteLog("⏱️ Đang kiểm tra quét tìm tệp ảnh thông báo [ok_final.png] trong 10.0 giây...", ConsoleColor.DarkGray);

                for (int checkCount = 0; checkCount < 20; checkCount++) // 20 lần * 500ms = 10 giây
                {
                    CaptureScreen(screenPath);
                    System.Drawing.Point? okFinalLocation = FindTemplate(screenPath, okFinalTemplate);

                    if (okFinalLocation != null)
                    {
                        okFinalDetected = true;
                        WriteLog($"🎯 Đã quét phát hiện thấy bảng thông báo chỉ hướng [ok_final.png]!", ConsoleColor.Green);

                        // TỐI ƯU HÓA: QUÉT TOÀN MÀN HÌNH GAME (Bên Trái, Bên Phải, Phía Trên, Phía Dưới)
                        WriteLog("📖 Đang phân tích chữ chỉ hướng trên toàn màn hình bằng OCR nhị phân...", ConsoleColor.DarkGray);
                        string textResult = RecognizeText(screenPath); // Truyền trực tiếp ảnh gốc màn hình

                        if (!string.IsNullOrEmpty(textResult))
                        {
                            WriteLog($"📝 OCR Nhận diện thành công! Nội dung chỉ dẫn: {textResult}", ConsoleColor.Cyan);
                            ParseOcrDirection(textResult); // Tự động rẽ nhánh hướng đi
                        }
                        else
                        {
                            WriteLog("⚠️ OCR không đọc được hướng đi nào rõ ràng.", ConsoleColor.Yellow);
                        }

                        // SAU KHI ĐỌC HƯỚNG XONG, TIẾN HÀNH ĐÓNG BẢNG LIÊN TIẾP CHUẨN XÁC: ok_final.png -> dong_dong.png
                        WriteLog($"✅ Click đóng [ok_final.png] tại: X={okFinalLocation.Value.X}, Y={okFinalLocation.Value.Y}", ConsoleColor.Green);
                        Tap(okFinalLocation.Value.X, okFinalLocation.Value.Y);
                        await Task.Delay(500, token); // Chờ bảng 1 đóng

                        // Quét tìm và đóng tiếp bảng rác dong_dong.png nếu xuất hiện
                        CaptureScreen(screenPath);
                        System.Drawing.Point? closeLoc = FindTemplate(screenPath, closeTemplate);
                        if (closeLoc != null)
                        {
                            WriteLog($"✅ Click đóng bảng phụ [dong_dong.png] tại: X={closeLoc.Value.X}, Y={closeLoc.Value.Y}", ConsoleColor.Green);
                            Tap(closeLoc.Value.X, closeLoc.Value.Y);
                            await Task.Delay(500, token); // Chờ bảng 2 đóng hoàn toàn
                        }
                        break;
                    }

                    await Task.Delay(500, token); // Đợi 500ms giữa mỗi lần quét
                }

                if (!okFinalDetected)
                {
                    // Nếu sau 10 giây không thấy nút ok_final.png hiện lên -> Đào thành công rương đồ!
                    isTreasureFound = true;
                }
                else
                {
                    isTreasureFound = false;
                }
            }

            return true;
        }

        // Bấm phím mũi tên XUỐNG 3 lần liên tục để tự động cuộn hành trang chạy ẩn mượt mà
        private static void ScrollInventoryDown()
        {
            for (int i = 0; i < 3; i++)
            {
                SendBackgroundKey(VK_DOWN, 150);
                Thread.Sleep(50);
            }
        }

        #endregion

        #region Công cụ hỗ trợ phân tích hướng đi của OCR

        // HÀM TỐI ƯU HÓA: Phân tích 8 hướng đi chéo/thẳng chi tiết và khoa học của Ninja School
        private static void ParseOcrDirection(string ocrText)
        {
            string text = ocrText.ToLower();

            // Kiểm tra các hướng kết hợp có độ ưu tiên cao trước (Chéo xuống, Chéo lên)
            if (text.Contains("trái") && text.Contains("dưới"))
            {
                nextDirection = 2; // TRÁI DƯỚI
            }
            else if (text.Contains("phải") && text.Contains("dưới"))
            {
                nextDirection = 5; // PHẢI DƯỚI
            }
            else if (text.Contains("trái") && text.Contains("trên"))
            {
                nextDirection = 7; // TRÁI TRÊN
            }
            else if (text.Contains("phải") && text.Contains("trên"))
            {
                nextDirection = 8; // PHẢI TRÊN
            }
            // Kiểm tra các hướng đơn lẻ tiếp theo
            else if (text.Contains("trái"))
            {
                nextDirection = 1; // TRÁI
            }
            else if (text.Contains("phải"))
            {
                nextDirection = 4; // PHẢI
            }
            else if (text.Contains("trên"))
            {
                nextDirection = 3; // PHÍA TRÊN
            }
            else if (text.Contains("dưới"))
            {
                nextDirection = 6; // PHÍA DƯỚI
            }
            else
            {
                nextDirection = 0; // Không nhận diện rõ -> Di chuyển ngẫu nhiên
            }
        }

        #endregion

        #region Công cụ hỗ trợ Đóng bảng quảng cáo/thông báo (dong_dong.png)

        // HÀM TỐI ƯU: Tự động check và click tắt bảng dong_dong.png trước mỗi tác vụ quan trọng
        private static void CheckAndDismissCloseButton(string screenPath, string closeTemplate)
        {
            CaptureScreen(screenPath);
            System.Drawing.Point? closeLoc = FindTemplate(screenPath, closeTemplate);
            if (closeLoc != null)
            {
                WriteLog("🧹 [Dọn dẹp] Phát hiện thấy bảng quảng cáo/thông báo che khuất [dong_dong.png]. Đang click đóng...", ConsoleColor.DarkGray);
                Tap(closeLoc.Value.X, closeLoc.Value.Y);
                Thread.Sleep(600); // Đợi bảng tắt hẳn
            }
        }

        // HÀM TỐI ƯU: Quét tìm đồng thời nút OK hoặc nút Đóng (dong_dong.png) để click đóng bảng chỉ hướng
        private static bool ClickOkOrCloseButton(string screenPath, string okTemplate, string closeTemplate)
        {
            System.Drawing.Point? okLoc = FindTemplate(screenPath, okTemplate);
            if (okLoc != null)
            {
                Tap(okLoc.Value.X, okLoc.Value.Y);
                return true;
            }

            System.Drawing.Point? closeLoc = FindTemplate(screenPath, closeTemplate);
            if (closeLoc != null)
            {
                Tap(closeLoc.Value.X, closeLoc.Value.Y);
                return true;
            }

            return false;
        }

        #endregion

        #region Công cụ gửi lệnh Bàn phím chạy ẩn hoàn toàn (PostMessage Win32)

        // Dò tìm cửa sổ con RenderWindow thực tế nằm sâu bên trong LDPlayer để gửi lệnh bàn phím ẩn
        private static IntPtr GetLdPlayerRenderHandle()
        {
            Process[] processes = Process.GetProcessesByName("dnplayer");
            if (processes.Length > 0)
            {
                IntPtr mainHandle = processes[0].MainWindowHandle;
                IntPtr renderHandle = FindWindowEx(mainHandle, IntPtr.Zero, "RenderWindow", null);
                if (renderHandle != IntPtr.Zero)
                {
                    return renderHandle;
                }
                return mainHandle;
            }
            return IntPtr.Zero;
        }

        // Gửi tín hiệu bàn phím chạy ẩn trực tiếp vào giả lập LDPlayer (Bỏ qua Focus)
        private static void SendBackgroundKey(byte virtualKeyCode, int durationMs)
        {
            IntPtr renderHandle = GetLdPlayerRenderHandle();
            if (renderHandle == IntPtr.Zero) return;

            // Gửi thông điệp nhấn phím ảo xuống (WM_KEYDOWN)
            PostMessage(renderHandle, WM_KEYDOWN, (IntPtr)virtualKeyCode, IntPtr.Zero);

            // Giữ phím trong thời gian yêu cầu
            Thread.Sleep(durationMs);

            // Gửi thông điệp nhả phím ảo lên (WM_KEYUP)
            PostMessage(renderHandle, WM_KEYUP, (IntPtr)virtualKeyCode, (IntPtr)0xC0000001);
        }

        // Nhấn giữ đồng thời hai phím chạy ẩn (Ví dụ: LÊN + TRÁI, LÊN + PHẢI)
        private static void HoldKeysTogether(byte key1, byte key2, int durationMs)
        {
            IntPtr renderHandle = GetLdPlayerRenderHandle();
            if (renderHandle == IntPtr.Zero) return;

            // Đồng thời nhấn giữ phím 1 và phím 2 xuống
            PostMessage(renderHandle, WM_KEYDOWN, (IntPtr)key1, IntPtr.Zero);
            PostMessage(renderHandle, WM_KEYDOWN, (IntPtr)key2, IntPtr.Zero);

            Thread.Sleep(durationMs);

            // Đồng thời thả cả 2 phím ra
            PostMessage(renderHandle, WM_KEYUP, (IntPtr)key1, (IntPtr)0xC0000001);
            PostMessage(renderHandle, WM_KEYUP, (IntPtr)key2, (IntPtr)0xC0000001);
        }

        #endregion

        #region Công cụ Ghi đè Log Động (Thread-Safe Status Overwrite)

        private static void WriteLog(string message, ConsoleColor color = ConsoleColor.White)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = color;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
                Console.ResetColor();
            }
        }

        private static void WriteStatus(string line1, string line2)
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"\r⏳ [Trạng thái] {line1,-60}\n");
                Console.Write($"\r🎮 [Bàn Phím]  {line2,-60}");
                Console.ResetColor();

                int targetTop = Console.CursorTop - 1;
                if (targetTop >= 0 && targetTop < Console.BufferHeight)
                {
                    Console.SetCursorPosition(0, targetTop);
                }
            }
        }

        private static void ClearStatusLines()
        {
            lock (_consoleLock)
            {
                Console.Write($"\r{new string(' ', 75)}\n");
                Console.Write($"\r{new string(' ', 75)}");

                int targetTop = Console.CursorTop - 1;
                if (targetTop >= 0 && targetTop < Console.BufferHeight)
                {
                    Console.SetCursorPosition(0, targetTop);
                }
            }
        }

        private static void ShowHeader()
        {
            lock (_consoleLock)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("==================================================================");
                Console.WriteLine("                 NINJA SCHOOL - AUTO TREASURE TOOLS               ");
                Console.ResetColor();
            }
        }

        #endregion

        #region Quản lý Cấu hình hệ thống

        private static void LoadConfiguration()
        {
            if (!Directory.Exists(imageFolder))
            {
                Directory.CreateDirectory(imageFolder);
            }

            if (File.Exists(configFile))
            {
                string[] lines = File.ReadAllLines(configFile);
                if (lines.Length >= 2)
                {
                    ldPlayerPath = lines[0].Trim();
                    deviceId = lines[1].Trim();
                    return;
                }
            }

            ldPlayerPath = @"E:\LDPlayer\LDPlayer9";
            deviceId = "emulator-5554";
            try
            {
                File.WriteAllLines(configFile, new[] { ldPlayerPath, deviceId });
                WriteLog($"📝 Đã khởi tạo tệp cấu hình '{configFile}' mặc định.", ConsoleColor.Yellow);
            }
            catch (Exception ex)
            {
                WriteLog($"Lỗi tạo file cấu hình: {ex.Message}", ConsoleColor.Red);
            }
        }

        private static bool ConfigureAdbPath()
        {
            if (string.IsNullOrEmpty(ldPlayerPath) || !Directory.Exists(ldPlayerPath))
            {
                WriteLog($"LỖI: Thư mục LDPlayer không tồn tại: {ldPlayerPath}", ConsoleColor.Red);
                return false;
            }

            string tempAdb = Path.Combine(ldPlayerPath, "adb.exe");
            if (File.Exists(tempAdb))
            {
                adbPath = tempAdb;
                return true;
            }

            tempAdb = Path.Combine(ldPlayerPath, "adb", "adb.exe");
            if (File.Exists(tempAdb))
            {
                adbPath = tempAdb;
                return true;
            }

            WriteLog("LỖI: Không thể tìm thấy adb.exe trong thư mục LDPlayer.", ConsoleColor.Red);
            return false;
        }

        private static void StartLdPlayer()
        {
            string dnPlayerExe = Path.Combine(ldPlayerPath, "dnplayer.exe");
            if (File.Exists(dnPlayerExe))
            {
                Process[] processes = Process.GetProcessesByName("dnplayer");
                if (processes.Length == 0)
                {
                    WriteLog("💻 Đang kích hoạt khởi động trình giả lập LDPlayer...", ConsoleColor.Yellow);
                    Process.Start(dnPlayerExe);
                    Thread.Sleep(5000);
                }
            }
        }

        #endregion

        #region Công cụ xử lý ngoại vi (ADB, OpenCV)

        private static void CaptureScreen(string savePath)
        {
            try
            {
                if (File.Exists(savePath)) File.Delete(savePath);
                RunAdbCommand("shell screencap -p /sdcard/screen.png");
                RunAdbCommand($"pull /sdcard/screen.png {savePath}");
            }
            catch { }
        }

        private static void Tap(int x, int y)
        {
            RunAdbCommand($"shell input tap {x} {y}");
        }

        private static void RunAdbCommand(string arguments)
        {
            if (string.IsNullOrEmpty(adbPath)) return;

            ProcessStartInfo psi = new ProcessStartInfo(adbPath, $"-s {deviceId} {arguments}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using (var process = Process.Start(psi))
            {
                process?.WaitForExit();
            }
        }

        private static System.Drawing.Point? FindTemplate(string screenPath, string templatePath)
        {
            if (!File.Exists(templatePath) || !File.Exists(screenPath)) return null;

            using (Mat screen = Cv2.ImRead(screenPath))
            using (Mat template = Cv2.ImRead(templatePath))
            using (Mat result = new Mat())
            {
                Cv2.MatchTemplate(screen, template, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);

                // Tối ưu hóa: Giảm ngưỡng nhận diện xuống 0.75 (75%) để bỏ qua số lượng đè lên góc dưới ô đồ
                if (maxVal >= 0.75)
                {
                    return new System.Drawing.Point(maxLoc.X + template.Width / 2, maxLoc.Y + template.Height / 2);
                }
            }
            return null;
        }

        private static void CropImage(string sourcePath, string destPath, System.Drawing.Rectangle cropArea)
        {
            if (!File.Exists(sourcePath)) return;

            using (Mat src = Cv2.ImRead(sourcePath))
            {
                int x = Math.Max(0, cropArea.X);
                int y = Math.Max(0, cropArea.Y);
                int width = Math.Min(cropArea.Width, src.Cols - x);
                int height = Math.Min(cropArea.Height, src.Rows - y);

                using (Mat cropped = new Mat(src, new OpenCvSharp.Rect(x, y, width, height)))
                {
                    Cv2.ImWrite(destPath, cropped);
                }
            }
        }

        private static string RecognizeText(string imagePath)
        {
            if (!Directory.Exists(tessdataPath) || !File.Exists(imagePath)) return string.Empty;

            try
            {
                using (var engine = new TesseractEngine(tessdataPath, "vie", EngineMode.Default))
                using (var img = Pix.LoadFromFile(imagePath))
                {
                    using (var page = engine.Process(img))
                    {
                        return page.GetText().Trim();
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        #endregion
    }
}