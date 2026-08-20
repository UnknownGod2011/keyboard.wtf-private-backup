export const runtimeConfig = Object.freeze({
  canonicalWebsite: "https://keyboardwtf.com",
  legacyWebsite: "https://keyboard-wtf.vercel.app",
  backendBaseUrl: "https://keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app",
  downloads: Object.freeze({
    windows: Object.freeze({
      available: true,
      version: "1.4.4",
      url: "https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.4/keyboard-wtf-setup.exe",
      sha256: "a78a0dd7a37b2abdc2da075f74314e4dcf790be9b4af41b02af625c784cd1d17",
    }),
    macos: Object.freeze({
      available: false,
      version: "",
      url: "",
      sha256: "",
      comingSoonMessage: "keyboard.wtf for macOS is coming soon. The Windows download is not compatible with macOS.",
    }),
  }),
  pricingDisplay: Object.freeze({
    asOf: "2026-08-16",
    baseAmountMinor: 99900,
    baseCurrency: "INR",
    approximateEquivalents: Object.freeze([
      "$10.50 USD",
      "€9.10 EUR",
      "£7.80 GBP",
      "CHF 8.50",
      "AED 38.50",
    ]),
  }),
  firebase: {
    apiKey: "AIzaSyATGk-af6CHF32qMdcWYcyOWCzFwsxYqW4",
    authDomain: "keyboard-wtf-prod.firebaseapp.com",
    projectId: "keyboard-wtf-prod",
    storageBucket: "keyboard-wtf-prod.firebasestorage.app",
    messagingSenderId: "952597522612",
    appId: "1:952597522612:web:cd0c3cc3630c095ec68fe1",
  },
});
