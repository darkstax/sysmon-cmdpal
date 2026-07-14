# Privacy Policy

Last updated: July 14, 2026

SysPulse for Command Palette processes system performance and hardware sensor data locally on the user's Windows device.

## Data Collection

SysPulse does not collect, transmit, sell, or share personal data, usage analytics, system metrics, hardware identifiers, or sensor readings.

## Local Data

- Settings are stored locally in the application's configuration directory.
- The optional SysMon Broker writes diagnostic logs locally under `%ProgramData%\SysMonCmdPal`.
- Sensor readings are exchanged locally between the optional Broker and the Command Palette extension through Windows shared memory.

## Network Access

SysPulse contacts GitHub only when the user explicitly starts the optional Broker install or update action. The application requests release metadata and the selected Broker asset from the project's official GitHub repository. No system metrics or personal data are included in those requests.

## Third-Party Services

GitHub processes release download requests according to the [GitHub Privacy Statement](https://docs.github.com/en/site-policy/privacy-policies/github-general-privacy-statement).

## Contact

Questions and privacy requests can be submitted through the project's [GitHub issue tracker](https://github.com/darkstax/sysmon-cmdpal/issues).
