import {
  api,
  auth,
  googleSignIn,
  listenForAuth,
  waitForAuthInitialization,
} from "./app.js";

const byId = (id) => document.getElementById(id);
const state = {
  items: [],
  nextCursor: null,
  ownReview: null,
  canModerate: false,
  authGeneration: 0,
  reviewCount: 0,
  averageRating: 0,
};

function stars(rating) {
  const safe = Math.max(0, Math.min(5, Math.round(Number(rating) || 0)));
  return `${"★".repeat(safe)}${"☆".repeat(5 - safe)}`;
}

function setPublicStatus(message, kind = "") {
  byId("reviewsStatus").textContent = message;
  byId("reviewsStatus").className = `status${kind ? ` ${kind}` : ""}`;
}

function setAuthStatus(message, kind = "") {
  byId("reviewAuthStatus").textContent = message;
  byId("reviewAuthStatus").className = `status${kind ? ` ${kind}` : ""}`;
}

function createReviewCard(review) {
  const card = document.createElement("article");
  card.className = "card review-card";
  const rating = document.createElement("div");
  rating.className = "review-stars";
  rating.textContent = stars(review.rating);
  rating.setAttribute("aria-label", `${review.rating} out of 5 stars`);
  const text = document.createElement("p");
  text.className = "review-text";
  text.textContent = review.text;
  const meta = document.createElement("div");
  meta.className = "review-meta";
  const identity = document.createElement("span");
  identity.textContent = review.reviewerDisplayName || "keyboard.wtf user";
  const date = document.createElement("time");
  date.dateTime = review.updatedAt;
  date.textContent = new Date(review.updatedAt).toLocaleDateString();
  meta.append(identity, date);
  card.append(rating, text, meta);

  if (state.canModerate) {
    const moderation = document.createElement("button");
    moderation.className = "secondary";
    moderation.type = "button";
    moderation.textContent = "Delete review";
    moderation.addEventListener("click", () => deleteReview(review));
    card.append(moderation);
  }
  return card;
}

function renderReviews() {
  const list = byId("reviewList");
  list.replaceChildren(...state.items.map(createReviewCard));
  const count = state.reviewCount;
  const average = state.averageRating;
  byId("averageRating").textContent = count ? average.toFixed(1) : "—";
  byId("averageStars").textContent = count ? "out of 5" : "";
  byId("reviewCount").textContent = count === 1 ? "1 review" : `${count} reviews`;
  byId("showMore").classList.toggle("hidden", !state.nextCursor);
  setPublicStatus(
    count ? "Showing current keyboard.wtf reviews." : "No reviews yet. Be the first to share one.",
    count ? "success" : "",
  );
}

async function loadReviews({ append = false } = {}) {
  const query = new URLSearchParams({ limit: "6" });
  if (append && state.nextCursor) query.set("cursor", state.nextCursor);
  const page = await api(`/v1/reviews?${query}`);
  state.items = append ? [...state.items, ...(page.items || [])] : (page.items || []);
  state.nextCursor = page.nextCursor || null;
  state.reviewCount = Number(page.reviewCount || 0);
  state.averageRating = Number(page.averageRating || 0);
  renderReviews();
}

async function loadOwnReview(generation) {
  const result = await api("/v1/reviews/me", { authenticated: true });
  if (generation !== state.authGeneration) return;
  state.ownReview = result.review || null;
  state.canModerate = result.canModerate === true;
  byId("reviewRating").value = String(state.ownReview?.rating || 5);
  byId("reviewText").value = state.ownReview?.text || "";
  byId("saveReview").textContent = state.ownReview ? "Update review" : "Publish review";
  byId("deleteOwnReview").classList.toggle("hidden", !state.ownReview);
  setAuthStatus(
    state.ownReview ? "Signed in. You can update your current review." : "Signed in. You may publish one current review.",
    "success",
  );
  renderReviews();
}

async function deleteReview(review) {
  if (!window.confirm("Delete this review? This action cannot be undone.")) return;
  try {
    const isOwn = state.ownReview?.reviewId === review.reviewId;
    await api(isOwn ? "/v1/reviews/me" : `/v1/reviews/${encodeURIComponent(review.reviewId)}`, {
      authenticated: true,
      method: "DELETE",
    });
    if (isOwn) state.ownReview = null;
    await Promise.all([loadOwnReview(state.authGeneration), loadReviews()]);
  } catch (error) {
    setAuthStatus(error.message, "error");
  }
}

byId("showMore").addEventListener("click", async () => {
  byId("showMore").disabled = true;
  try {
    await loadReviews({ append: true });
  } catch (error) {
    setPublicStatus(error.message, "error");
  } finally {
    byId("showMore").disabled = false;
  }
});

byId("reviewSignIn").addEventListener("click", async () => {
  byId("reviewSignIn").disabled = true;
  setAuthStatus("Opening Google sign-in…");
  try {
    await googleSignIn();
  } catch (error) {
    setAuthStatus(error.message, "error");
  } finally {
    byId("reviewSignIn").disabled = false;
  }
});

byId("reviewForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  byId("saveReview").disabled = true;
  setAuthStatus("Saving your review…");
  try {
    await api("/v1/reviews/me", {
      authenticated: true,
      method: "PUT",
      body: JSON.stringify({
        rating: Number(byId("reviewRating").value),
        text: byId("reviewText").value,
      }),
    });
    await Promise.all([loadOwnReview(state.authGeneration), loadReviews()]);
  } catch (error) {
    setAuthStatus(error.message, "error");
  } finally {
    byId("saveReview").disabled = false;
  }
});

byId("deleteOwnReview").addEventListener("click", async () => {
  if (!state.ownReview || !window.confirm("Delete your review?")) return;
  byId("deleteOwnReview").disabled = true;
  try {
    await api("/v1/reviews/me", { authenticated: true, method: "DELETE" });
    state.ownReview = null;
    await Promise.all([loadOwnReview(state.authGeneration), loadReviews()]);
  } catch (error) {
    setAuthStatus(error.message, "error");
  } finally {
    byId("deleteOwnReview").disabled = false;
  }
});

const initialReviews = loadReviews().catch((error) => setPublicStatus(error.message, "error"));

listenForAuth(async (user) => {
  const generation = ++state.authGeneration;
  await initialReviews;
  byId("reviewSignedOut").classList.toggle("hidden", Boolean(user));
  byId("reviewForm").classList.toggle("hidden", !user);
  if (!user) {
    state.ownReview = null;
    state.canModerate = false;
    setAuthStatus("Sign in with Google to add or manage your review.");
    renderReviews();
    return;
  }
  setAuthStatus("Loading your review…");
  try {
    await loadOwnReview(generation);
  } catch (error) {
    if (generation === state.authGeneration) setAuthStatus(error.message, "error");
  }
});

await waitForAuthInitialization();
await initialReviews;
