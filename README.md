# Sticker Print — Firebase Auto Print

แอป Windows (.NET 10, WinForms) สำหรับรับงานพิมพ์จาก **Cloud Firestore** แบบ realtime แล้วพิมพ์สติกเกอร์ออกเครื่องพิมพ์ที่เลือกโดยอัตโนมัติ
แต่ละเครื่อง PC ทำหน้าที่เป็น **1 Printer Station**

## ข้อมูลบนสติกเกอร์

| บนสติกเกอร์ | ฟิลด์ใน Firestore (ค่าเริ่มต้น) |
|---|---|
| Ticket code (มุมขวาบน) | `ticket_code` |
| คำนำหน้า + ชื่อ-นามสกุล (ตัวใหญ่) | `title` + `fullname` (ถ้า `fullname` ว่างจะใช้ `first_name` + `last_name`) |
| ตำแหน่ง | `position` |
| องค์กร | `organization` |
| Category (แถบดำล่างสุด) | `category` |

เปลี่ยนชื่อฟิลด์ได้ในแท็บ **ตั้งค่า → Field mapping** และเปิด/ปิดแต่ละบรรทัดได้ในหัวข้อ **รูปแบบ**
ขนาดตัวอักษรจะปรับอัตโนมัติให้พอดีกับขนาดสติกเกอร์ และรองรับภาษาไทย

## Printer Station (1 PC = 1 Station)

| เครื่อง | ตั้งค่า Printer Station # | ผลลัพธ์ |
|---|---|---|
| PC-A | 1 | พิมพ์เฉพาะ job ที่ `printerId = 1` |
| PC-B | 2 | พิมพ์เฉพาะ job ที่ `printerId = 2` |
| PC-C | 3 | พิมพ์เฉพาะ job ที่ `printerId = 3` |

- ทุกเครื่องใช้ project / collection เดียวกัน (ค่าเริ่มต้น `campaign-th---game` / `print_jobs`)
- `printerId` จะเก็บเป็นตัวเลข (`1`) หรือข้อความ (`"1"`) ก็ได้
- เปลี่ยนชื่อฟิลด์ที่ใช้กรองได้ (เช่น `station`) และถ้าเว้นว่าง เครื่องนั้นจะรับทุก job
- ถ้าข้อมูลในระบบยังใช้ `station: "main"` (ไม่มี `printerId`) ให้ตั้ง **ฟิลด์ที่ใช้กรอง Station** = `station` และ **Printer Station #** = `main`

### สถานะของ job

1. งานที่ยังไม่พิมพ์ = `status == "pending"` (ค่าเริ่มต้น) หรือเลือก `printed == false` ในแท็บตั้งค่าได้
2. ก่อนพิมพ์ แอปจะ claim งานแบบ transaction (`status = "printing"`) จึงไม่มีการพิมพ์ซ้ำแม้มีหลายเครื่อง
3. พิมพ์สำเร็จ → `printed = true`, `status = "printed"`, `printed_at`, `printed_by`, `printed_station`, `printer`
4. พิมพ์ไม่สำเร็จ → `status = "error"`, `error_message` (สั่งพิมพ์ซ้ำได้จากหน้าจอ)
5. ถ้าต้องการพิมพ์ซ้ำจากฝั่ง backend ให้ตั้ง `status` กลับเป็น `"pending"` (หรือ `printed = false`)

> Service account ต้องมีสิทธิ์อ่าน/เขียน Firestore (เช่น role **Cloud Datastore User**)
> ถ้าปิด "mark printed กลับ Firestore" แอปจะจำ ID ที่พิมพ์แล้วไว้ในเครื่องแทน

## การใช้งาน

1. ดาวน์โหลด `StickerPrintApp.exe` จาก GitHub Actions (artifact `StickerPrintApp-win-x64`) หรือ build เอง (ด้านล่าง)
2. วางไฟล์ Service account `campaign-th---game-xxxx.json` ไว้ข้าง `.exe` (แอปจะหาเจอเอง) หรือกด **เลือกไฟล์...** ในแท็บ **ตั้งค่า**
3. ตั้งค่า **Printer Station #** ของเครื่องนี้ (1 / 2 / 3)
4. เลือก **เครื่องพิมพ์** และ **ขนาดสติกเกอร์** (หรือ "กำหนดเอง" แล้วใส่กว้าง × สูง mm) ดูตัวอย่างได้ทันทีทางขวา
5. กด **บันทึกการตั้งค่า** → **เชื่อมต่อ** แล้วกด **พิมพ์ทดสอบ** เพื่อเช็กตำแหน่ง
   - ถ้าพิมพ์เยื้อง ปรับ **ชดเชยตำแหน่ง X / Y**
   - ถ้าเป็นเครื่องพิมพ์สติกเกอร์ (Zebra, Godex, TSC, Xprinter ฯลฯ) ควรตั้งขนาด Stock ในไดรเวอร์ให้ตรงกับขนาดสติกเกอร์ด้วย
6. เปิด **พิมพ์อัตโนมัติ** ไว้ เมื่อมี job ใหม่เข้ามา แอปจะพิมพ์ออกทันที
   - ถ้าปิดไว้ job จะขึ้นเป็น "รอสั่งพิมพ์" ให้เลือกแล้วกด **พิมพ์ / พิมพ์ซ้ำ รายการที่เลือก**

### การเชื่อมต่อ

- **Realtime** (ค่าเริ่มต้น) ใช้ Firestore listener (gRPC) ได้งานทันที
- **Polling** ดึงข้อมูลทุก N วินาทีผ่าน HTTPS ปกติ สำหรับเครือข่ายหรือ proxy ที่บล็อก gRPC
  (ถ้าโหมด Realtime ไม่ได้รับข้อมูลภายใน 20 วินาที แอปจะแจ้งใน log)

ไฟล์ตั้งค่าและ log อยู่ที่ `%AppData%\StickerPrintApp\` (`settings.json`, `logs\yyyy-MM-dd.log`)

## Build

ต้องมี [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
dotnet build -c Release
# exe ไฟล์เดียว ไม่ต้องติดตั้ง .NET บนเครื่องปลายทาง
dotnet publish src/StickerPrintApp -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## ความปลอดภัย

**ห้าม commit ไฟล์ Service account (.json) เข้า Git** (`.gitignore` กันไว้แล้ว) ให้คัดลอกไฟล์ไปที่เครื่อง Station โดยตรงเท่านั้น

## โครงสร้างโค้ด

| ไฟล์ | หน้าที่ |
|---|---|
| `FirestoreJobSource.cs` | เชื่อม Firestore, กรองตาม Station, claim / mark printed |
| `JobProcessor.cs` | คิวพิมพ์ทีละงาน (claim → print → write back) |
| `StickerRenderer.cs` | วาดเลย์เอาต์สติกเกอร์ (ใช้ทั้งพิมพ์จริงและ preview) |
| `StickerPrinter.cs` | ส่งงานไปเครื่องพิมพ์ด้วยขนาดกระดาษที่กำหนด |
| `MainForm.cs` | หน้าจอหลัก / ตั้งค่า / preview |
| `AppSettings.cs` | ค่าตั้งค่า (บันทึกใน `%AppData%`) |
