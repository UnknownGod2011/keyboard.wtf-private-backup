import { runtimeConfig } from "./config.js";

const windows = runtimeConfig.downloads.windows;
const windowsButton = document.getElementById("windowsDownload");
const windowsRelease = document.getElementById("windowsRelease");
if (windows.available && windows.url) {
  windowsButton.href = windows.url;
  windowsButton.textContent = "Download for Windows";
  windowsButton.removeAttribute("aria-disabled");
  windowsRelease.textContent = `Version ${windows.version} for Windows 10 and 11 x64. The initial release is not Authenticode-signed, so Windows may show an unknown-publisher warning.`;
}

const macos = runtimeConfig.downloads.macos;
const macButton = document.getElementById("macDownload");
if (macos.available && macos.url) {
  macButton.disabled = false;
  macButton.textContent = "Download for macOS";
  macButton.addEventListener("click", () => window.location.assign(macos.url));
}
