# Shared UI resources

Reusable WPF themes, tokens, controls, icons and behaviors with no feature business state.

`src/FgoPet.UiSdk` owns shared hosting contracts and the theme service/settings types. `src/FgoPet.Platform/Contracts` owns the persisted pure `AppTheme` enum. Each type has one compilation owner.

Resource source files remain in this directory. SettingsControls, ChatControls and ShellTokens compile in UiSdk. Other existing application theme/control resources retain their App resource paths for compatibility. Physical source location does not imply a second assembly.

No business module or host implementation is a dependency of UiSdk. Theme selection remains deterministic; resources contain no credentials, user data or prompts.

Validation: ThemeServiceTests, ThemeResourceTests and ChatButtonTemplateTests in the Windows test project; settings coordinator coverage in the SettingsHost test project; Release solution build and evaluated architecture checks.
