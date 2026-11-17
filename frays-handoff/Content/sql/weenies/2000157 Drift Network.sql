-- @owner: frays
-- @owns: wcid:2000157
-- @requires-restart: no
--
-- Frays: the way back from the Fray village (by where 2000075 lands, 0xD5DA 179.5, 166.6) to the Drift Network hall (0x00070145 70, -85).

DELETE FROM `weenie` WHERE `class_Id` = 2000157;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000157, 'dw-drift-network-return-portal', 7, '2026-11-02 18:13:54') /* Portal */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000157,   1,      65536) /* ItemType - Portal */
     , (2000157,  16,         32) /* ItemUseable - Remote */
     , (2000157,  93,       3084) /* PhysicsState - Ethereal, ReportCollisions, Gravity, LightingOn */
     , (2000157, 111,          1) /* PortalBitmask - Unrestricted */
     , (2000157, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000157, 9022,          1) /* PortalRealm */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000157,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000157,  54,    -0.1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000157,   1, 'Drift Network') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000157,   1, 0x020005D3) /* Setup */
     , (2000157,   2, 0x09000003) /* MotionTable */
     , (2000157,   8, 0x0600106B) /* Icon */;

INSERT INTO `weenie_properties_position` (`object_Id`, `position_Type`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000157, 2, 0x00070145, 70, -85, 0.005, 0, 0, 0, 1) /* Destination */
/* @teleloc 0x00070145 [70.000000 -85.000000 0.005000] 0.000000 0.000000 0.000000 1.000000 */;
