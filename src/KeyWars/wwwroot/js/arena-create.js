import { attachPersonPickers } from "./person-picker.js";

attachPersonPickers();

document.querySelectorAll("[data-arena-create-form]").forEach((form) => {
  const visibility = form.querySelector("[data-arena-visibility]");
  const invitations = form.querySelector("[data-arena-invitations]");
  const updateVisibility = () => {
    const invitationOnly = visibility?.value === "InvitationOnly";
    invitations?.classList.toggle("is-hidden", !invitationOnly);
    invitations?.querySelectorAll("input[type='hidden'][name='Input.InvitationProfileIds']")
      .forEach((input) => { input.disabled = !invitationOnly; });
  };

  visibility?.addEventListener("change", updateVisibility);
  updateVisibility();
});
