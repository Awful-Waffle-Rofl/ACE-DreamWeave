-- @owner: frays
-- @owns: wcid:2000150
-- @requires-restart: no
--
-- Frays: the left of three portals inside the Temple of Xik Minru; to the west of Vissidal; lands on the north beach of 0xC9E1 (0xC9E10028 96, 178), a landblock with no fire.

DELETE FROM `weenie` WHERE `class_Id` = 2000150;

INSERT INTO `weenie` (`class_Id`, `class_Name`, `type`, `last_Modified`)
VALUES (2000150, 'dw-fray-west-portal', 7, '2026-11-02 14:04:02') /* Portal */;

INSERT INTO `weenie_properties_int` (`object_Id`, `type`, `value`)
VALUES (2000150,   1,      65536) /* ItemType - Portal */
     , (2000150,  16,         32) /* ItemUseable - Remote */
     , (2000150,  86,        300) /* MinLevel */
     , (2000150,  93,       3084) /* PhysicsState - Ethereal, ReportCollisions, Gravity, LightingOn */
     , (2000150, 111,          1) /* PortalBitmask - Unrestricted */
     , (2000150, 133,          4) /* ShowableOnRadar - ShowAlways */
     , (2000150, 9022,          1) /* PortalRealm */;

INSERT INTO `weenie_properties_bool` (`object_Id`, `type`, `value`)
VALUES (2000150,   1, True ) /* Stuck */;

INSERT INTO `weenie_properties_float` (`object_Id`, `type`, `value`)
VALUES (2000150,  54,    -0.1) /* UseRadius */;

INSERT INTO `weenie_properties_string` (`object_Id`, `type`, `value`)
VALUES (2000150,   1, 'Western Shore') /* Name */;

INSERT INTO `weenie_properties_d_i_d` (`object_Id`, `type`, `value`)
VALUES (2000150,   1, 0x020005D5) /* Setup */
     , (2000150,   2, 0x09000003) /* MotionTable */
     , (2000150,   8, 0x0600106B) /* Icon */;

INSERT INTO `weenie_properties_position` (`object_Id`, `position_Type`, `obj_Cell_Id`, `origin_X`, `origin_Y`, `origin_Z`, `angles_W`, `angles_X`, `angles_Y`, `angles_Z`)
VALUES (2000150, 2, 0xC9E10028, 96, 178, 0.5, 1, 0, 0, 0) /* Destination */
/* @teleloc 0xC9E10028 [96.000000 178.000000 0.500000] 1.000000 0.000000 0.000000 0.000000 */;
