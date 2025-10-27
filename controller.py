import sys
import subprocess
import json
import time
from pathlib import Path
from typing import List

# --- 配置 ---
# Python 脚本的执行目录
PROJECT_DIR = Path(__file__).parent.resolve()
# 任务文件由 C# 创建在上一级目录
ALBUM_JSON_FILE = PROJECT_DIR.parent / "album.json"
# 【正确】扫描和验证的下载根目录
SCAN_DIR = PROJECT_DIR / "AM-DL downloads"


def run_downloader_in_wsl(url: str) -> bool:
    """运行 Go 程序，并返回其是否成功退出。"""
    command = ["go", "run", "main.go", url]
    print(f"   Executing command in WSL: {' '.join(command)}")
    process = None
    try:
        process = subprocess.Popen(
            command,
            cwd=PROJECT_DIR,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
        )
        print("   --- Downloader program output ---")
        for line in iter(process.stdout.readline, ""):
            current_line = line.strip()
            # 过滤掉下载进度条，保持日志干净
            if "%" not in current_line and "MB/s" not in current_line and current_line:
                print(f"     {current_line}")

        return_code = process.wait()
        print("   -----------------------")

        if return_code == 0:
            print("   Downloader process finished with exit code 0 (Success).")
            return True
        else:
            print(f"!! Downloader process failed with exit code: {return_code}")
            return False

    except Exception as e:
        print(f"!! CRITICAL ERROR while trying to run the downloader: {e}")
        return False


def find_incremental_files(start_time: float) -> List[Path]:
    """通过时间戳，查找新生成的文件用于校验。"""
    if not SCAN_DIR.exists():
        return []

    new_files = []
    print(f"-> Scanning for new files in '{SCAN_DIR}'...")
    try:
        # 扫描 .m4a 和 .flac 文件，与您之前的脚本逻辑一致
        all_media_files = list(SCAN_DIR.rglob("*.m4a")) + list(SCAN_DIR.rglob("*.flac"))
        for f in all_media_files:
            # 检查文件修改时间是否晚于任务开始时间
            if f.is_file() and f.stat().st_mtime > start_time:
                new_files.append(f)
    except Exception as e:
        print(f"!! Error while scanning for new files: {e}")

    return new_files


def validate_files_with_ffmpeg(files_to_validate: List[Path]) -> bool:
    """
    使用 ffmpeg 校验文件。
    如果发现损坏文件，会将其删除。
    """
    if not files_to_validate:
        print("   No new files found to validate. Assuming success.")
        return True

    print(f"   Found {len(files_to_validate)} new file(s) to validate with ffmpeg.")
    all_valid = True
    for media_file in files_to_validate:
        command = [
            "ffmpeg",
            "-v",
            "error",
            "-i",
            str(media_file),
            "-f",
            "null",
            "-",
            "-xerror",
        ]
        try:
            subprocess.run(command, check=True, capture_output=True, text=True)
            print(f"     [OK] {media_file.name}")
        except FileNotFoundError:
            print(
                "!! CRITICAL ERROR: 'ffmpeg' command not found in WSL. Cannot validate files."
            )
            return False  # 如果 ffmpeg 不存在，整个校验失败
        except subprocess.CalledProcessError as e:
            print(f"     [CORRUPTED] {media_file.name}")
            print(f"       Reason: {e.stderr.strip()}")
            all_valid = False
            try:
                media_file.unlink()
                print(f"       -> Deleted corrupted file: {media_file.name}")
            except Exception as del_e:
                print(f"       -> Failed to delete corrupted file: {del_e}")

    return all_valid


def main():
    """主程序入口。"""
    print("--- Python Download Controller (with FFMPEG Validation) ---")

    if not ALBUM_JSON_FILE.exists():
        print("No 'album.json' found. Exiting gracefully.")
        print("\nExit Finished!")
        sys.exit(0)

    try:
        with open(ALBUM_JSON_FILE, "r", encoding="utf-8") as f:
            task_data = json.load(f)

        album_url = task_data.get("album_url")
        album_name = task_data.get("album_name", "Unknown Album")

        if not album_url:
            print("!! CRITICAL ERROR: 'album.json' is missing 'album_url'.")
            sys.exit(1)

        print(f"-> Starting download for: '{album_name}'")
        print(f"   URL: {album_url}")

        task_start_time = time.time()

        # 步骤 1: 运行 Go 下载器
        go_success = run_downloader_in_wsl(album_url)
        if not go_success:
            print("\n!! [FAILURE] The download process failed during execution.")
            sys.exit(1)

        # 步骤 2: 如果 Go 成功，立刻进行 FFMPEG 校验
        print("\n-> Download process reported success. Starting FFMPEG validation...")
        time.sleep(2)  # 等待文件系统I/O

        newly_created_files = find_incremental_files(task_start_time)
        validation_success = validate_files_with_ffmpeg(newly_created_files)

        # 步骤 3: 根据校验结果决定最终状态
        if validation_success:
            print("\n[SUCCESS] FFMPEG validation passed. All files are valid.")
            print("\nExit Finished!")
            sys.exit(0)
        else:
            # 这个输出会被 C# 捕获并分类为 ValidationErr
            print(
                "\n!! [FAILURE] FFMPEG validation failed. One or more files were corrupted."
            )
            sys.exit(1)

    except Exception as e:
        print(f"!! An unexpected error occurred in the Python controller: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
