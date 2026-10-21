# DiskMount98

mochimochi-man (X:@MochimochimanEX)

[日本語](README.jp.md)

A Windows tool that mounts PC-98 DOS floppy images as a drive, so you can work with them in Explorer. No driver installation is required.

## Supported formats

* NFD (r0 / r1), D88, HDM
* DOS-formatted FAT12/16 disks (2HD 1.25MB / 1.44MB, 2DD 640KB / 720KB, etc.)

## Download

Download `DiskMount98.exe` from [Releases](../../releases). No installation is required.

## Requirements

Windows 10 / 11 (.NET Framework 4.x is included with Windows)

## Usage

1. Drop an image file onto `DiskMount98.exe`, or launch the exe and choose a file.
2. The image is mounted on a free drive letter (starting from Z:), and Explorer opens.
3. Any changes you make to files are written back to the image automatically.
4. Click the "Unmount" button in the small window to finish.

Dropping several files mounts each one on its own drive.

## Notes

* Before the first write, the original image is saved as `<filename>.bak`.
* Names that don't fit the 8.3 format are converted automatically (e.g. `New Text Document.txt` → `NEWTEX~1.TXT`).
* Files you don't change stay where they are on the disk, so a boot disk stays bootable after editing.
* If there isn't enough space, the image is left unchanged and an error is shown in the small window.
* Write-protected images are mounted read-only.
* The free space Explorer shows is not the image's free space. Check the small window for the real value.

## Build

Run `build.bat`. It builds with `csc.exe` (.NET Framework 4), which comes with Windows, so Visual Studio is not needed.

## How it works

The files in the image are extracted to a temporary folder, and a drive letter is assigned to that folder. When the folder changes, the FAT is updated and only the changed sectors are written back to the image.

## License

[MIT License](LICENSE)
