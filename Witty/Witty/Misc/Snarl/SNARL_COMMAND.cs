
namespace Snarl
{
	internal enum SNARL_COMMAND: short
	{
		SNARL_SHOW = 1,
		SNARL_HIDE,
		SNARL_UPDATE,
		SNARL_IS_VISIBLE,
		SNARL_GET_VERSION,
		SNARL_REGISTER_CONFIG_WINDOW,
		SNARL_REVOKE_CONFIG_WINDOW,
		SNARL_REGISTER_ALERT,
		SNARL_REVOKE_ALERT,
		SNARL_REGISTER_CONFIG_WINDOW_2 = 0x0A,
		SNARL_GET_VERSION_EX,
		SNARL_SET_TIMEOUT,
		SNARL_SHOW_EX = 0x20
	}
}
