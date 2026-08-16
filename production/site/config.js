export const runtimeConfig = Object.freeze({
  canonicalWebsite: "https://keyboardwtf.com",
  legacyWebsite: "https://keyboard-wtf.vercel.app",
  backendBaseUrl: "https://keyboard-wtf-paid-api-mhrc6c7goa-el.a.run.app",
  downloads: Object.freeze({
    windows: Object.freeze({
      available: true,
      version: "1.4.0",
      url: "https://github.com/UnknownGod2011/keyboard.wtf/releases/download/v1.4.0/keyboard-wtf-setup.exe",
      sha256: "99791ff5beeee767cdaba3fc244bdd3314a451dda222979ecd9d627b821cd458",
    }),
    macos: Object.freeze({
      available: false,
      version: "",
      url: "",
      sha256: "",
    }),
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
